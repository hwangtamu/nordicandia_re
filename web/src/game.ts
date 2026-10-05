// M1 Babylon scene: the exported Unity dungeon kit + billboarded avatar tokens, top-down
// camera. Rendering only — position, HP, damage, kills and experience all come from the
// server-authoritative combat instance via the hooks below. The browser sends intent
// (move/skill) and interpolates the returned snapshots for smoothness.

import {
  ArcRotateCamera,
  Color3,
  Color4,
  DirectionalLight,
  Engine,
  HemisphericLight,
  Light,
  Matrix,
  Mesh,
  MeshBuilder,
  Nullable,
  PointerEventTypes,
  PointLight,
  Scene,
  SceneLoader,
  SpotLight,
  StandardMaterial,
  Texture,
  TransformNode,
  Vector3,
} from "@babylonjs/core";
import "@babylonjs/loaders/glTF";

import { CombatEnvelope, CombatEvent, CombatSnapshot, LootDrop, MapLayout, MonsterState } from "./api";
import { ContentManifest, loadManifest, monsterIcon, raceIcon } from "./content";

export interface HudState {
  health: number;
  maxHealth: number;
  mana: number;
  maxMana: number;
  shield: number;
  level: number;
  experience: number;
  experienceForLevel: number;
  experienceForNextLevel: number;
  silver: number;
  opals: number;
  monsterKills: number;
  monstersAlive: number;
  dungeonsCleared: number;
  bossAlive: boolean;
  bossKillsRemaining: number;
  packsRemaining: number;
  totalPacks: number;
  niflheimExitReady: boolean;
  autoMove: boolean;
  inTown: boolean;
  skills: { name: string; effect: string; ready: boolean; cooldown: number; manaCost: number }[];
  message: string;
}

export interface WorldHooks {
  onHud: (hud: HudState) => void;
  onLoot: (loot: LootDrop[]) => void;
  /** Returns the latest authoritative state, or null while offline. */
  pollState: () => Promise<CombatEnvelope | null>;
  moveTo: (x: number, z: number) => void;
  castSkill: (skillId: number) => void;
  onTownNpc: (npc: string) => void;
  onNiflheimExit: () => void;
  onNiflheimChest: () => void;
}

interface Entity {
  root: TransformNode;
  target: Vector3;
  lastHp: number;
  alive: boolean;
  moving: boolean;
  stepPhase: number;
  level: number;
  monsterName?: string;
  isBoss?: boolean;
  rarity?: number;
}

interface TransientVisual {
  mesh: Mesh;
  material: StandardMaterial;
  kind: string;
}

interface TownSpawnZone {
  name: string;
  x: number;
  z: number;
}

interface TownSceneManifest {
  spawnZones: TownSpawnZone[];
  lights: { name: string; type: number; x: number; y: number; z: number; direction: number[];
    color: number[]; intensity: number; range: number; spotAngle: number }[];
}

interface TownNpcLabel {
  element: HTMLButtonElement;
  position: Vector3;
}

const KIT = "/assets/kit/dungeon_default/";
const ROOM_HALF = 18;
const FLOOR_SPACING = 6;
const POLL_INTERVAL = 0.15;

export class World {
  private readonly engine: Engine;
  private readonly scene: Scene;
  private readonly camera: ArcRotateCamera;
  private readonly canvas: HTMLCanvasElement;
  private readonly overlay: HTMLDivElement;
  private readonly content: ContentManifest;
  private readonly hooks: WorldHooks;
  private readonly templates = new Map<string, TransformNode>();
  private readonly monsterEntities = new Map<number, Entity>();
  private readonly transientEntities = new Map<string, TransientVisual>();
  private readonly townNpcActions = new Map<Mesh, string>();
  private exitPortalMesh: Mesh | null = null;
  private exitChestMesh: Mesh | null = null;
  private townNpcRoots: TransformNode[] = [];
  private townNpcLabels: TownNpcLabel[] = [];
  private townLights: Light[] = [];
  private mapTiles: TransformNode[] = [];
  private mapSignature = "";
  private ground: Nullable<Mesh> = null;
  private townGround: Nullable<Mesh> = null;
  private defaultRoomRoot: Nullable<TransformNode> = null;
  private townSceneRoot: Nullable<TransformNode> = null;
  private townSceneLoading: Promise<void> | null = null;
  private player!: Entity;
  private floaters: { el: HTMLDivElement; world: Vector3; born: number; ttl: number }[] = [];
  private latest: CombatSnapshot | null = null;
  private version = 0;
  private pollTimer = 0;
  private polling = false;
  private elapsed = 0;
  private hudTimer = 0;
  private message = "";
  private messageUntil = 0;
  private disposed = false;
  private autoMove = true;
  private resizeHandler = (): void => this.engine.resize();

  constructor(
    canvas: HTMLCanvasElement,
    overlay: HTMLDivElement,
    content: ContentManifest,
    race: number,
    hooks: WorldHooks,
  ) {
    this.canvas = canvas;
    this.overlay = overlay;
    this.content = content;
    this.hooks = hooks;

    this.engine = new Engine(canvas, true, { preserveDrawingBuffer: true, stencil: true });
    this.scene = new Scene(this.engine);
    this.scene.clearColor = new Color4(0.05, 0.06, 0.08, 1);

    this.camera = new ArcRotateCamera("camera", -Math.PI / 2, 0.95, 30, Vector3.Zero(), this.scene);
    this.camera.lowerBetaLimit = 0.7;
    this.camera.upperBetaLimit = 1.15;
    this.camera.lowerRadiusLimit = 16;
    this.camera.upperRadiusLimit = 44;
    this.camera.wheelPrecision = 8;
    this.camera.attachControl(canvas, true);
    this.camera.inputs.removeByType("ArcRotateCameraPointersInput");

    const ambient = new HemisphericLight("ambient", new Vector3(0.3, 1, 0.1), this.scene);
    ambient.intensity = 0.95;
    ambient.diffuse = new Color3(0.85, 0.82, 0.75);
    ambient.groundColor = new Color3(0.18, 0.16, 0.14);

    this.playerRace = race;
  }

  private readonly playerRace: number;

  async start(initial: CombatEnvelope): Promise<void> {
    await this.loadKit();
    this.buildRoom();
    this.buildPlayer();
    this.buildMonsters(initial.combat);
    this.pushState(initial);
    this.bindInput();
    this.scene.onBeforeRenderObservable.add(() => this.update(this.engine.getDeltaTime() / 1000));
    this.engine.runRenderLoop(() => {
      if (!this.disposed) this.scene.render();
    });
    window.addEventListener("resize", this.resizeHandler);
    this.engine.resize();
  }

  toggleAutoMove(): boolean {
    this.autoMove = !this.autoMove;
    this.say(this.autoMove ? "Auto-move on" : "Auto-move off");
    return this.autoMove;
  }

  dispose(): void {
    this.disposed = true;
    this.engine.stopRenderLoop();
    this.scene.dispose();
    this.engine.dispose();
    this.overlay.querySelectorAll(".floater, .town-npc").forEach((el) => el.remove());
    window.removeEventListener("resize", this.resizeHandler);
  }

  // ------------------------------------------------------------------ content

  private async loadKit(): Promise<void> {
    const names = [
      "Floor_Slab_lrg",
      "MOD_Wall_01_O_straight_large",
      "MOD_Column_01_large",
      "SM_PROP_brazier_dungeon_02",
    ];
    // Mesh -> base-colour texture mapping exported alongside the kit.
    let textureByMesh = new Map<string, string>();
    try {
      const manifest = await loadManifest();
      textureByMesh = new Map(manifest.kit.meshes.filter((m) => m.texture).map((m) => [m.name, m.texture as string]));
    } catch (error) {
      console.warn("[kit] manifest unavailable, using plain materials", error);
    }
    await Promise.all(
      names.map(async (name) => {
        try {
          const result = await SceneLoader.ImportMeshAsync("", KIT, `${name}.glb`, this.scene);
          const root = result.meshes[0] as TransformNode;
          const texture = textureByMesh.get(name);
          let material: StandardMaterial | null = null;
          if (texture) {
            material = new StandardMaterial(`${name}_mat`, this.scene);
            material.diffuseTexture = new Texture(`${KIT}textures/${texture}.png`, this.scene);
            material.specularColor = new Color3(0.04, 0.04, 0.04);
            material.ambientColor = new Color3(0.4, 0.4, 0.4);
          }
          result.meshes.forEach((mesh) => {
            mesh.isPickable = false;
            if (material && mesh.getTotalVertices() > 0) {
              mesh.material = material;
            } else if (mesh.material) {
              (mesh.material as StandardMaterial).specularColor = new Color3(0.05, 0.05, 0.05);
            }
          });
          this.templates.set(name, root);
        } catch (error) {
          console.warn(`[kit] failed to load ${name}`, error);
        }
      }),
    );
  }

  private instantiate(name: string, position: Vector3, rotationY = 0, scale = 1): TransformNode | null {
    const template = this.templates.get(name);
    if (!template) return null;
    const clone = template.clone(`${name}_instance`, null, false);
    if (!clone) return null;
    clone.position = position;
    clone.rotation = new Vector3(0, rotationY, 0);
    if (scale !== 1) clone.scaling = new Vector3(scale, scale, scale);
    clone.getChildMeshes().forEach((mesh) => {
      mesh.isPickable = false;
    });
    return clone;
  }

  /** W04/W05: assemble the authoritative grid or the exported Unity Town scene. */
  private buildMapLayout(layout: MapLayout): void {
    this.mapTiles.forEach((t) => t.dispose(false, true));
    this.mapTiles = [];
    if (layout.theme === "town_hub") {
      this.camera.radius = 25;
      this.defaultRoomRoot?.setEnabled(false);
      if (this.ground) { this.ground.isVisible = false; this.ground.isPickable = false; }
      this.scene.clearColor = new Color4(0.2, 0.27, 0.34, 1);
      if (!this.townGround) {
        this.townGround = MeshBuilder.CreateGround("town_input_ground",
          { width: 2 * ROOM_HALF, height: 2 * ROOM_HALF }, this.scene);
        const material = new StandardMaterial("town_input_ground_material", this.scene);
        material.alpha = 0.001;
        material.disableLighting = true;
        this.townGround.material = material;
        this.townGround.isPickable = true;
      }
      void this.showTownScene();
      return;
    }

    this.townSceneRoot?.setEnabled(false);
    this.camera.radius = 30;
    if (this.townGround) { this.townGround.dispose(false, true); this.townGround = null; }
    if (this.ground) { this.ground.isVisible = true; this.ground.isPickable = true; }
    this.defaultRoomRoot?.setEnabled(false);
    this.scene.clearColor = new Color4(0.05, 0.06, 0.08, 1);
    const tile = (2 * ROOM_HALF) / Math.max(layout.width, layout.height);
    const scale = Math.max(0.2, tile / FLOOR_SPACING);
    const at = (gx: number, gz: number) =>
      new Vector3((gx - layout.width / 2 + 0.5) * tile, 0.02, (gz - layout.height / 2 + 0.5) * tile);
    const floor = (gx: number, gz: number) =>
      gx >= 0 && gx < layout.width && gz >= 0 && gz < layout.height && layout.rows[gz][gx] === "#";
    for (let gz = 0; gz < layout.height; gz++) {
      for (let gx = 0; gx < layout.width; gx++) {
        if (!floor(gx, gz)) continue;
        const slab = this.instantiate("Floor_Slab_lrg", at(gx, gz), 0, scale);
        if (slab) this.mapTiles.push(slab);
      }
    }
    // Wall ring: void cells orthogonally adjacent to floor.
    for (let gz = 0; gz < layout.height; gz++) {
      for (let gx = 0; gx < layout.width; gx++) {
        if (floor(gx, gz)) continue;
        if (!(floor(gx - 1, gz) || floor(gx + 1, gz) || floor(gx, gz - 1) || floor(gx, gz + 1))) continue;
        const pos = at(gx, gz);
        pos.y = 0;
        const wall = this.instantiate("MOD_Wall_01_O_straight_large", pos, 0, scale);
        if (wall) this.mapTiles.push(wall);
      }
    }
  }

  private async showTownScene(): Promise<void> {
    if (!this.townSceneRoot && !this.townSceneLoading) {
      this.townSceneLoading = (async () => {
        const response = await fetch("/assets/kit/town/town_scene.json");
        if (!response.ok) throw new Error("Town scene manifest missing");
        const manifest = await response.json() as TownSceneManifest;
        const imported = await SceneLoader.ImportMeshAsync("", "/assets/kit/town/", "town_scene.glb", this.scene);
        const root = new TransformNode("town_scene_root", this.scene);
        for (const mesh of imported.meshes) {
          mesh.isPickable = false;
          if (!mesh.parent) mesh.parent = root;
        }
        this.townSceneRoot = root;
        this.createTownLights(manifest.lights ?? []);
        this.createTownMarkers(manifest.spawnZones);
      })().catch((error: unknown) => {
        console.warn("[town] failed to load the original town scene", error);
        if (this.latest?.inTown && this.ground) {
          this.ground.isVisible = true;
          this.ground.isPickable = true;
          this.defaultRoomRoot?.setEnabled(true);
        }
      }).finally(() => { this.townSceneLoading = null; });
    }
    if (this.townSceneLoading) await this.townSceneLoading;
    this.townSceneRoot?.setEnabled(Boolean(this.latest?.inTown));
  }

  private createTownLights(rows: TownSceneManifest["lights"]): void {
    for (const light of this.townLights) light.dispose();
    this.townLights = [];
    for (const row of rows) {
      const position = new Vector3(row.x, row.y, row.z);
      const direction = new Vector3(row.direction[0], row.direction[1], row.direction[2]);
      let light: Light;
      if (row.type === 1) {
        light = new DirectionalLight(`town_${row.name}`, direction, this.scene);
      } else if (row.type === 0) {
        light = new SpotLight(`town_${row.name}`, position, direction, row.spotAngle * Math.PI / 180, 2, this.scene);
        light.range = row.range;
      } else if (row.type === 2) {
        const point = new PointLight(`town_${row.name}`, position, this.scene);
        point.range = row.range;
        light = point;
      } else {
        continue; // baked area lights have no stable web equivalent
      }
      light.diffuse = new Color3(row.color[0], row.color[1], row.color[2]);
      light.intensity = Math.min(2, Math.max(0, row.intensity * 0.2));
      light.parent = this.townSceneRoot;
      this.townLights.push(light);
    }
  }

  private createTownMarkers(spawnZones: TownSpawnZone[]): void {
    const stations: Record<string, { action: string; label: string; icon: string; color: Color3 }> = {
      Blacksmith: { action: "blacksmith", label: "Blacksmith", icon: "Blacksmith.png", color: new Color3(1, 0.58, 0.22) },
      Disassembler: { action: "disassembler", label: "Disassembler", icon: "Blacksmith.png", color: new Color3(1, 0.58, 0.22) },
      Merchant: { action: "merchant", label: "Merchant", icon: "Merchant.png", color: new Color3(0.92, 0.82, 0.3) },
      Petkeeper: { action: "petkeeper", label: "Petkeeper", icon: "Petkeeper.png", color: new Color3(0.63, 0.85, 0.5) },
      CombatPetkeeper: { action: "combat-petkeeper", label: "Combat Petkeeper", icon: "CombatPetKeeper.png", color: new Color3(0.65, 0.75, 1) },
      BattlePetkeeper_1: { action: "combat-petkeeper", label: "Combat Petkeeper", icon: "CombatPetKeeper.png", color: new Color3(0.65, 0.75, 1) },
      SetItemMerchant: { action: "set-merchant", label: "Set Merchant", icon: "SetMerchant.png", color: new Color3(0.8, 0.65, 1) },
      Offering: { action: "offering", label: "Offering", icon: "Offering.png", color: new Color3(0.6, 0.85, 1) },
      GiftStatue: { action: "offering", label: "Gift Statue", icon: "Offering.png", color: new Color3(0.6, 0.85, 1) },
      PortalMaster: { action: "portal", label: "Portal Master", icon: "PortalMaster.png", color: new Color3(0.4, 0.8, 1) },
      TownPortal: { action: "town-portal", label: "Town Portal", icon: "PortalMaster.png", color: new Color3(0.4, 0.8, 1) },
      WorldPortal: { action: "worlds", label: "World Portal", icon: "PortalMaster.png", color: new Color3(0.4, 0.8, 1) },
    };
    this.clearTownMarkers();
    for (const spawn of spawnZones) {
      const station = stations[spawn.name];
      if (!station) continue; // NPCs without an implemented non-placeholder function stay hidden.
      const root = new TransformNode(`town_npc_${spawn.name}`, this.scene);
      root.parent = this.townSceneRoot;
      root.position = new Vector3(spawn.x, 0, spawn.z);
      root.scaling.setAll(0.5);
      this.addToken(root, `/assets/avatars/${station.icon}`, station.color);
      const hotspot = MeshBuilder.CreateSphere(`town_npc_hotspot_${spawn.name}`, { diameter: 2.8, segments: 8 }, this.scene);
      hotspot.parent = root;
      hotspot.position.y = 0.9;
      hotspot.isPickable = true;
      const material = new StandardMaterial(`${hotspot.name}_material`, this.scene);
      material.alpha = 0.001;
      material.disableLighting = true;
      hotspot.material = material;
      this.townNpcActions.set(hotspot, station.action);
      const label = document.createElement("button");
      label.type = "button";
      label.className = "town-npc";
      label.dataset.action = station.action;
      label.textContent = station.label;
      label.addEventListener("click", (event) => {
        event.stopPropagation();
        this.hooks.onTownNpc(station.action);
      });
      this.overlay.appendChild(label);
      this.townNpcLabels.push({ element: label, position: new Vector3(spawn.x, 2.5, spawn.z) });
      this.townNpcRoots.push(root);
    }
  }

  private updateTownNpcLabels(): void {
    const townVisible = Boolean(this.latest?.inTown && this.townSceneRoot?.isEnabled());
    const viewport = this.camera.viewport.toGlobal(this.engine.getRenderWidth(), this.engine.getRenderHeight());
    const placed: { x: number; y: number; width: number; height: number }[] = [];
    const offsets: [number, number][] = [[0, 0], [0, -32], [84, -12], [-84, -12],
      [0, 32], [150, 0], [-150, 0], [84, 28], [-84, 28], [0, -64], [0, 64]];
    for (const row of this.townNpcLabels) {
      row.element.hidden = !townVisible;
      if (!townVisible) continue;
      const screen = Vector3.Project(row.position, Matrix.Identity(), this.scene.getTransformMatrix(), viewport);
      const width = Math.max(72, (row.element.textContent ?? "").length * 7 + 20);
      const height = 24;
      let x = screen.x, y = screen.y;
      for (const [dx, dy] of offsets) {
        const candidateX = screen.x + dx;
        const candidateY = screen.y + dy;
        const collision = placed.some(p => Math.abs(p.x - candidateX) < (p.width + width) / 2 + 4
          && Math.abs(p.y - candidateY) < (p.height + height) / 2 + 4);
        if (!collision) { x = candidateX; y = candidateY; break; }
      }
      placed.push({ x, y, width, height });
      row.element.style.transform = `translate(-50%, -100%) translate(${x}px, ${y}px)`;
      const inView = screen.z >= 0 && screen.z <= 1;
      row.element.style.opacity = inView ? "1" : "0";
      row.element.style.pointerEvents = inView ? "auto" : "none";
    }
  }

  private clearTownMarkers(): void {
    for (const root of this.townNpcRoots) root.dispose(false, true);
    for (const label of this.townNpcLabels) label.element.remove();
    this.townNpcRoots = [];
    this.townNpcLabels = [];
    this.townNpcActions.clear();
  }

  private buildRoom(): void {
    this.defaultRoomRoot = new TransformNode("default_room_root", this.scene);
    const groundMat = new StandardMaterial("groundMat", this.scene);
    groundMat.diffuseColor = new Color3(0.22, 0.19, 0.16);
    groundMat.specularColor = new Color3(0, 0, 0);
    const ground = MeshBuilder.CreateGround("ground", { width: 2 * ROOM_HALF + 6, height: 2 * ROOM_HALF + 6 }, this.scene);
    ground.material = groundMat;
    ground.isPickable = true;
    this.ground = ground;

    const count = Math.floor((2 * ROOM_HALF) / FLOOR_SPACING) + 1;
    for (let ix = 0; ix < count; ix++) {
      for (let iz = 0; iz < count; iz++) {
        this.instantiateDefaultRoom("Floor_Slab_lrg", new Vector3(-ROOM_HALF + ix * FLOOR_SPACING, 0.02, -ROOM_HALF + iz * FLOOR_SPACING));
      }
    }

    const edge = ROOM_HALF + 3;
    for (let i = -2; i <= 2; i++) {
      const along = i * 4;
      this.instantiateDefaultRoom("MOD_Wall_01_O_straight_large", new Vector3(along - 2, 0, -edge), 0);
      this.instantiateDefaultRoom("MOD_Wall_01_O_straight_large", new Vector3(along - 2, 0, edge), Math.PI);
      this.instantiateDefaultRoom("MOD_Wall_01_O_straight_large", new Vector3(-edge, 0, along - 2), Math.PI / 2);
      this.instantiateDefaultRoom("MOD_Wall_01_O_straight_large", new Vector3(edge, 0, along - 2), -Math.PI / 2);
    }
    for (const [x, z] of [[-ROOM_HALF, -ROOM_HALF], [ROOM_HALF, -ROOM_HALF], [-ROOM_HALF, ROOM_HALF], [ROOM_HALF, ROOM_HALF]] as [number, number][]) {
      this.instantiateDefaultRoom("MOD_Column_01_large", new Vector3(x, 0, z));
      this.addBrazier(x * 0.72, z * 0.72);
    }
    this.addBrazier(0, -ROOM_HALF + 2);
    this.addBrazier(0, ROOM_HALF - 2);
  }

  private instantiateDefaultRoom(name: string, position: Vector3, rotationY = 0): void {
    const item = this.instantiate(name, position, rotationY);
    if (item && this.defaultRoomRoot) item.parent = this.defaultRoomRoot;
  }

  private addBrazier(x: number, z: number): void {
    this.instantiateDefaultRoom("SM_PROP_brazier_dungeon_02", new Vector3(x, 0, z));
    const light = new PointLight(`brazier_${x}_${z}`, new Vector3(x, 1.4, z), this.scene);
    light.parent = this.defaultRoomRoot;
    light.diffuse = new Color3(1, 0.6, 0.25);
    light.intensity = 1.6;
    light.range = 14;
  }

  private addToken(root: TransformNode, textureUrl: string, ringColor: Color3): void {
    const disc = MeshBuilder.CreateCylinder(`${root.name}_disc`, { diameter: 2.1, height: 0.08, tessellation: 24 }, this.scene);
    disc.parent = root;
    disc.position.y = 0.05;
    disc.isPickable = false;
    const discMat = new StandardMaterial(`${root.name}_discMat`, this.scene);
    discMat.diffuseColor = new Color3(0.05, 0.05, 0.06);
    disc.material = discMat;

    const ring = MeshBuilder.CreateTorus(`${root.name}_ring`, { diameter: 2.5, thickness: 0.14, tessellation: 32 }, this.scene);
    ring.parent = root;
    ring.position.y = 0.12;
    ring.isPickable = false;
    const ringMat = new StandardMaterial(`${root.name}_ringMat`, this.scene);
    ringMat.emissiveColor = ringColor;
    ringMat.diffuseColor = ringColor;
    ring.material = ringMat;

    const material = new StandardMaterial(`token_${textureUrl}`, this.scene);
    material.diffuseTexture = new Texture(textureUrl, this.scene);
    material.diffuseTexture.hasAlpha = true;
    material.useAlphaFromDiffuseTexture = true;
    material.specularColor = new Color3(0, 0, 0);
    material.emissiveColor = new Color3(0.35, 0.35, 0.35);
    material.backFaceCulling = false;
    const face = MeshBuilder.CreatePlane(`${root.name}_face`, { size: 1.9 }, this.scene);
    face.parent = root;
    face.rotation.x = Math.PI / 2;
    face.position.y = 0.14;
    face.isPickable = false;
    face.material = material;
  }

  private buildPlayer(): void {
    const root = new TransformNode("player", this.scene);
    this.addToken(root, raceIcon(this.content, this.playerRace), new Color3(0.35, 0.8, 1));
    this.player = { root, target: Vector3.Zero(), lastHp: 0, alive: true, moving: false, stepPhase: 0, level: 1 };
  }

  private buildMonsters(initial: CombatSnapshot): void {
    this.syncMonsters(initial.monsters);
  }

  private createMonsterEntity(monster: MonsterState): Entity {
    const root = new TransformNode(`monster_${monster.index}`, this.scene);
    const matching = this.content.monsters.find((m) => m.name === monster.name);
    const fallback = this.content.monsters.length > 0
      ? this.content.monsters[monster.index % this.content.monsters.length]
      : undefined;
    const iconMonster = monster.isBoss ? this.content.monsters[0] : (matching ?? fallback);
    const icon = iconMonster
      ? monsterIcon(this.content, iconMonster)
      : "/assets/avatars/MonstersAvatarIcons_48.png";
    const rarityColor = monster.rarity === 1 ? new Color3(0.25, 0.55, 1)
      : monster.rarity === 2 ? new Color3(1, 0.85, 0.25)
      : monster.rarity === 4 ? new Color3(0.75, 0.35, 1) : new Color3(0.9, 0.25, 0.2);
    this.addToken(root, icon, monster.isBoss ? new Color3(0.95, 0.75, 0.2) : rarityColor);
    if (monster.isBoss) root.scaling = new Vector3(1.6, 1.6, 1.6);
    return {
      root,
      target: new Vector3(monster.x, 0, monster.z),
      lastHp: monster.hp,
      alive: monster.alive,
      moving: false,
      stepPhase: Math.random() * Math.PI * 2,
      level: monster.level,
      monsterName: monster.name,
      isBoss: monster.isBoss,
      rarity: monster.rarity ?? 0,
    };
  }

  private syncMonsters(monsters: MonsterState[]): void {
    const present = new Set<number>();
    for (const monster of monsters) {
      present.add(monster.index);
      let entity = this.monsterEntities.get(monster.index);
      if (entity && (entity.monsterName !== monster.name || entity.isBoss !== monster.isBoss || entity.rarity !== (monster.rarity ?? 0))) {
        entity.root.dispose(false, true);
        this.monsterEntities.delete(monster.index);
        entity = undefined;
      }
      if (!entity) {
        entity = this.createMonsterEntity(monster);
        this.monsterEntities.set(monster.index, entity);
      }
    }
    for (const [index, entity] of this.monsterEntities) {
      if (present.has(index)) continue;
      entity.root.dispose(false, true);
      this.monsterEntities.delete(index);
    }
  }

  private createTransient(key: string, kind: string): TransientVisual {
    let mesh: Mesh;
    let color: Color3;
    let alpha = 1;
    if (kind === "cloud") {
      mesh = MeshBuilder.CreateCylinder(key, { height: 0.08, diameter: 1, tessellation: 32 }, this.scene);
      color = new Color3(0.2, 0.95, 0.55);
      alpha = 0.28;
    } else if (kind === "trap" || kind === "channel") {
      mesh = MeshBuilder.CreateTorus(key, { diameter: 1, thickness: 0.07, tessellation: 24 }, this.scene);
      color = kind === "trap" ? new Color3(1, 0.55, 0.12) : new Color3(0.35, 0.75, 1);
      alpha = kind === "channel" ? 0.65 : 0.9;
    } else {
      mesh = MeshBuilder.CreateSphere(key, { diameter: 1, segments: 10 }, this.scene);
      color = kind === "projectile" ? new Color3(0.3, 0.9, 1) : new Color3(0.75, 0.55, 1);
    }
    mesh.isPickable = false;
    mesh.position.y = kind === "cloud" || kind === "trap" || kind === "channel" ? 0.08 : 0.55;
    const material = new StandardMaterial(`${key}_material`, this.scene);
    material.diffuseColor = color;
    material.emissiveColor = color.scale(kind === "cloud" ? 0.45 : 0.8);
    material.alpha = alpha;
    material.disableLighting = true;
    mesh.material = material;
    return { mesh, material, kind };
  }

  private upsertTransient(key: string, kind: string, x: number, z: number, size: number): void {
    let visual = this.transientEntities.get(key);
    if (visual && visual.kind !== kind) {
      visual.mesh.dispose(false, true);
      this.transientEntities.delete(key);
      visual = undefined;
    }
    if (!visual) {
      visual = this.createTransient(key, kind);
      this.transientEntities.set(key, visual);
    }
    visual.mesh.position.x = x;
    visual.mesh.position.z = z;
    if (kind === "cloud" || kind === "trap" || kind === "channel") {
      visual.mesh.scaling.x = Math.max(0.2, size * 2);
      visual.mesh.scaling.z = Math.max(0.2, size * 2);
    } else {
      const diameter = kind === "projectile" ? Math.max(0.25, size * 2) : 0.85;
      visual.mesh.scaling.setAll(diameter);
    }
  }

  private syncTransientEntities(state: CombatSnapshot): void {
    const present = new Set<string>();
    for (const effect of state.groundEffects ?? []) {
      const key = `cloud:${effect.id}`;
      present.add(key);
      this.upsertTransient(key, "cloud", effect.x, effect.z, effect.radius);
    }
    for (const minion of state.minions ?? []) {
      if (!minion.alive) continue;
      const key = `minion:${minion.id}`;
      present.add(key);
      this.upsertTransient(key, "minion", minion.x, minion.z, 0.45);
    }
    for (const projectile of state.projectiles ?? []) {
      const key = `projectile:${projectile.id}`;
      present.add(key);
      this.upsertTransient(key, "projectile", projectile.x, projectile.z, projectile.radius);
    }
    for (const trap of state.traps ?? []) {
      const key = `trap:${trap.id}`;
      present.add(key);
      this.upsertTransient(key, "trap", trap.x, trap.z, trap.radius);
    }
    if (state.activeChannel) {
      present.add("channel:active");
      this.upsertTransient("channel:active", "channel", state.playerX, state.playerZ, state.activeChannel.radius);
    }
    for (const [key, visual] of this.transientEntities) {
      if (present.has(key)) continue;
      visual.mesh.dispose(false, true);
      this.transientEntities.delete(key);
    }
  }

  private presentEvents(events: CombatEvent[]): void {
    for (const event of events) {
      if (event.type === "heal" && event.amount > 0) {
        this.addFloater(this.player.root.position, `+${Math.round(event.amount)}`, "#76e9b2");
      } else if (event.type === "summon" && event.amount > 0) {
        this.addFloater(this.player.root.position, `SUMMON ×${Math.round(event.amount)}`, "#c7a6ff");
      } else if (event.type === "cooldown-reset") {
        this.say(`${event.detail} cooldown reset`);
      }
    }
  }

  private syncNiflheimExit(state: CombatSnapshot): void {
    if (!state.niflheimExitReady) {
      this.exitPortalMesh?.dispose(false, true);
      this.exitChestMesh?.dispose(false, true);
      this.exitPortalMesh = this.exitChestMesh = null;
      return;
    }
    if (!this.exitPortalMesh) {
      const portal = MeshBuilder.CreateCylinder("niflheim_town_portal",
        { diameter: 2.2, height: 0.08, tessellation: 32 }, this.scene);
      portal.rotation.x = Math.PI / 2;
      const material = new StandardMaterial("niflheim_town_portal_material", this.scene);
      material.diffuseColor = new Color3(0.25, 0.7, 1);
      material.emissiveColor = new Color3(0.15, 0.55, 1);
      material.alpha = 0.55;
      portal.material = material;
      const ring = MeshBuilder.CreateTorus("niflheim_town_portal_ring",
        { diameter: 2.2, thickness: 0.23, tessellation: 32 }, this.scene);
      ring.parent = portal;
      ring.isPickable = false;
      ring.material = material;
      this.exitPortalMesh = portal;
    }
    this.exitPortalMesh.position.set(state.niflheimExitX, 1.25, state.niflheimExitZ);
    if (state.niflheimChestSize > 0 && !state.niflheimChestOpened) {
      if (!this.exitChestMesh) {
        const chest = MeshBuilder.CreateBox("niflheim_loot_chest",
          { width: 1.3, height: 1.0, depth: 0.9 }, this.scene);
        const material = new StandardMaterial("niflheim_loot_chest_material", this.scene);
        material.diffuseColor = state.niflheimChestSize === 2 ? new Color3(0.8, 0.55, 0.15) : new Color3(0.48, 0.3, 0.14);
        material.emissiveColor = new Color3(0.22, 0.14, 0.04);
        chest.material = material;
        this.exitChestMesh = chest;
      }
      this.exitChestMesh.position.set(state.niflheimChestX, 0.6, state.niflheimChestZ);
    } else {
      this.exitChestMesh?.dispose(false, true);
      this.exitChestMesh = null;
    }
  }

  // ------------------------------------------------------------------ input

  private bindInput(): void {
    this.scene.onPointerObservable.add((info) => {
      if (info.type !== PointerEventTypes.POINTERDOWN) return;
      const event = info.event as PointerEvent;
      if (event.button !== 0) return;
      const pick = this.scene.pick(this.scene.pointerX, this.scene.pointerY, (mesh) =>
        mesh === this.ground || mesh === this.townGround || this.townNpcActions.has(mesh as Mesh)
        || mesh === this.exitPortalMesh || mesh === this.exitChestMesh);
      if (!pick?.hit) return;
      if (pick.pickedMesh === this.exitPortalMesh) { this.hooks.onNiflheimExit(); return; }
      if (pick.pickedMesh === this.exitChestMesh) { this.hooks.onNiflheimChest(); return; }
      const townNpc = pick.pickedMesh ? this.townNpcActions.get(pick.pickedMesh as Mesh) : undefined;
      if (townNpc) {
        this.hooks.onTownNpc(townNpc);
      } else if (pick.pickedPoint) {
        this.hooks.moveTo(pick.pickedPoint.x, pick.pickedPoint.z);
        this.say(this.latest?.inTown ? "Moving in Town" : "Moving");
      }
    });
  }

  // ------------------------------------------------------------------ state

  currentVersion(): number {
    return this.version;
  }

  pushState(envelope: CombatEnvelope): void {
    const state = envelope.combat;
    // Commands return the latest snapshot; a slower in-flight poll must not roll it back.
    if (state.version < this.version) return;
    const previous = this.latest;
    this.latest = state;
    this.version = state.version;
    this.player.target = new Vector3(state.playerX, 0, state.playerZ);

    // W04: rebuild the dungeon when the authoritative layout changes.
    if (envelope.map) {
      const signature = `${envelope.map.theme}:${envelope.map.width}x${envelope.map.height}:${envelope.map.rows.join("")}`;
      if (signature !== this.mapSignature) {
        this.mapSignature = signature;
        this.buildMapLayout(envelope.map);
      }
    }

    if (previous && previous.playerHp > state.playerHp) {
      this.addFloater(this.player.root.position, `-${Math.round(previous.playerHp - state.playerHp)}`, "#ff6b6b");
    }
    if (previous && !previous.inTown && state.inTown) this.say("Town hub · safe zone");
    if (previous && previous.inTown && !state.inTown) this.say("Returned to the world");
    if (previous && previous.playerLevel < state.playerLevel) {
      this.say(`Level up! Level ${state.playerLevel}`);
    }
    if (previous && previous.bossKillsRemaining > 0 && state.bossAlive && !previous.bossAlive) {
      this.say("A boss has appeared!");
    }
    if (previous && previous.dungeonsCleared < state.dungeonsCleared) {
      this.say(`Dungeon cleared! (${state.dungeonsCleared})`);
    }
    if (envelope.loot.length > 0) this.hooks.onLoot(envelope.loot);
    this.syncMonsters(state.monsters);
    this.syncNiflheimExit(state);
    this.syncTransientEntities(state);
    this.presentEvents(state.events ?? []);

    for (const monster of state.monsters) {
      const entity = this.monsterEntities.get(monster.index);
      if (!entity) continue;
      entity.target = new Vector3(monster.x, 0, monster.z);
      entity.level = monster.level;
      if (monster.alive && previous) {
        const before = previous.monsters.find((m) => m.index === monster.index);
        if (before && before.alive && monster.hp < before.hp) {
          this.addFloater(entity.root.position, `-${Math.round(before.hp - monster.hp)}`, "#ffe9c7");
        }
        if (before && before.alive && !monster.alive) {
          this.addFloater(entity.root.position, monster.isBoss ? "BOSS DOWN" : "KILL", "#9be36b");
        }
      }
      entity.lastHp = monster.hp;
      entity.alive = monster.alive;
      entity.root.setEnabled(monster.alive);
    }
    this.hudDirty = true;
  }

  // ------------------------------------------------------------------ update

  private update(dt: number): void {
    if (dt <= 0) return;
    this.elapsed += dt;
    this.pollTimer += dt;
    if (this.pollTimer >= POLL_INTERVAL && !this.polling) {
      this.pollTimer = 0;
      this.polling = true;
      this.hooks
        .pollState()
        .then((state) => {
          if (state) this.pushState(state);
        })
        .catch(() => undefined)
        .finally(() => {
          this.polling = false;
        });
    }

    this.interpolate(this.player, dt);
    for (const monster of this.monsterEntities.values()) this.interpolate(monster, dt);
    this.updateTownNpcLabels();
    this.updateFloaters();

    const target = this.player.target;
    this.camera.target = Vector3.Lerp(this.camera.target, new Vector3(target.x, 0, target.z), Math.min(1, dt * 4));

    this.hudTimer += dt;
    if (this.hudTimer > 0.1 || this.hudDirty) {
      this.hudTimer = 0;
      this.hudDirty = false;
      this.emitHud();
    }
  }

  private interpolate(entity: Entity, dt: number): void {
    const current = entity.root.position;
    const delta = entity.target.subtract(current);
    delta.y = 0;
    const distance = delta.length();
    if (distance < 0.03) {
      entity.moving = false;
      entity.root.position.y = 0;
      return;
    }
    const step = Math.min(distance, dt * 14);
    entity.root.position.addInPlace(delta.normalize().scale(step));
    entity.root.rotation.y = Math.atan2(delta.x, delta.z);
    entity.moving = true;
    entity.stepPhase += dt * 10;
    entity.root.position.y = Math.abs(Math.sin(entity.stepPhase)) * 0.12;
  }

  // ------------------------------------------------------------------ fx / hud

  private addFloater(world: Vector3, text: string, color: string): void {
    const el = document.createElement("div");
    el.className = "floater";
    el.textContent = text;
    el.style.color = color;
    this.overlay.appendChild(el);
    this.floaters.push({ el, world: world.add(new Vector3(0, 1.8, 0)), born: performance.now(), ttl: 850 });
  }

  private updateFloaters(): void {
    const now = performance.now();
    const keep: typeof this.floaters = [];
    for (const floater of this.floaters) {
      const age = now - floater.born;
      if (age > floater.ttl) {
        floater.el.remove();
        continue;
      }
      const screen = Vector3.Project(
        floater.world,
        Matrix.Identity(),
        this.scene.getTransformMatrix(),
        this.camera.viewport.toGlobal(this.engine.getRenderWidth(), this.engine.getRenderHeight()),
      );
      floater.el.style.transform = `translate(-50%, -50%) translate(${screen.x}px, ${screen.y}px)`;
      floater.el.style.opacity = String(1 - age / floater.ttl);
      keep.push(floater);
    }
    this.floaters = keep;
  }

  private say(message: string): void {
    this.message = message;
    this.messageUntil = this.elapsed + 2.5;
    this.hudDirty = true;
  }

  private emitHud(): void {
    const state = this.latest;
    if (!state) return;
    const monstersAlive = state.monsters.filter((m) => m.alive).length;
    this.hooks.onHud({
      health: Math.round(state.playerHp),
      maxHealth: Math.round(state.playerMaxHp),
      mana: Math.round(state.playerMana),
      maxMana: Math.round(state.playerMaxMana),
      shield: Math.round(state.playerShield),
      level: state.playerLevel,
      experience: state.experience,
      experienceForLevel: experienceForLevel(Math.floor(state.playerLevel)),
      experienceForNextLevel: experienceForLevel(Math.floor(state.playerLevel) + 1),
      silver: state.silver,
      opals: state.opals,
      monsterKills: state.kills,
      monstersAlive,
      dungeonsCleared: state.dungeonsCleared,
      bossAlive: state.bossAlive,
      bossKillsRemaining: state.bossKillsRemaining,
      packsRemaining: state.packsRemaining,
      totalPacks: state.totalPacks,
      niflheimExitReady: state.niflheimExitReady,
      autoMove: this.autoMove,
      inTown: state.inTown,
      skills: (state.skills ?? []).map((skill) => ({ name: skill.name, effect: skill.effect, ready: skill.cooldown <= 0, cooldown: skill.cooldown, manaCost: skill.manaCost })),
      message: this.elapsed < this.messageUntil ? this.message : "",
    });
  }

  private hudDirty = true;
}

// Local copy of the client-verified curve for the XP bar; the server remains authoritative.
function experienceForLevel(level: number): number {
  return level <= 1 ? 0 : 350 + 20 * Math.pow(level, 1.7);
}
