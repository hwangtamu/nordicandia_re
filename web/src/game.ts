// M1 Babylon scene: the exported Unity dungeon kit + billboarded avatar tokens, top-down
// camera. Rendering only — position, HP, damage, kills and experience all come from the
// server-authoritative combat instance via the hooks below. The browser sends intent
// (move/skill) and interpolates the returned snapshots for smoothness.

import {
  ArcRotateCamera,
  Color3,
  Color4,
  Engine,
  HemisphericLight,
  Matrix,
  Mesh,
  MeshBuilder,
  Nullable,
  PointerEventTypes,
  PointLight,
  Scene,
  SceneLoader,
  StandardMaterial,
  Texture,
  TransformNode,
  Vector3,
} from "@babylonjs/core";
import "@babylonjs/loaders/glTF";

import { CombatEnvelope, CombatSnapshot, LootDrop, MonsterState } from "./api";
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
  autoMove: boolean;
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
}

interface Entity {
  root: TransformNode;
  target: Vector3;
  lastHp: number;
  alive: boolean;
  moving: boolean;
  stepPhase: number;
  level: number;
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
  private ground: Nullable<Mesh> = null;
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
    this.overlay.querySelectorAll(".floater").forEach((el) => el.remove());
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

  private instantiate(name: string, position: Vector3, rotationY = 0): TransformNode | null {
    const template = this.templates.get(name);
    if (!template) return null;
    const clone = template.clone(`${name}_instance`, null, false);
    if (!clone) return null;
    clone.position = position;
    clone.rotation = new Vector3(0, rotationY, 0);
    clone.getChildMeshes().forEach((mesh) => {
      mesh.isPickable = false;
    });
    return clone;
  }

  private buildRoom(): void {
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
        this.instantiate("Floor_Slab_lrg", new Vector3(-ROOM_HALF + ix * FLOOR_SPACING, 0.02, -ROOM_HALF + iz * FLOOR_SPACING));
      }
    }

    const edge = ROOM_HALF + 3;
    for (let i = -2; i <= 2; i++) {
      const along = i * 4;
      this.instantiate("MOD_Wall_01_O_straight_large", new Vector3(along - 2, 0, -edge), 0);
      this.instantiate("MOD_Wall_01_O_straight_large", new Vector3(along - 2, 0, edge), Math.PI);
      this.instantiate("MOD_Wall_01_O_straight_large", new Vector3(-edge, 0, along - 2), Math.PI / 2);
      this.instantiate("MOD_Wall_01_O_straight_large", new Vector3(edge, 0, along - 2), -Math.PI / 2);
    }
    for (const [x, z] of [[-ROOM_HALF, -ROOM_HALF], [ROOM_HALF, -ROOM_HALF], [-ROOM_HALF, ROOM_HALF], [ROOM_HALF, ROOM_HALF]] as [number, number][]) {
      this.instantiate("MOD_Column_01_large", new Vector3(x, 0, z));
      this.addBrazier(x * 0.72, z * 0.72);
    }
    this.addBrazier(0, -ROOM_HALF + 2);
    this.addBrazier(0, ROOM_HALF - 2);
  }

  private addBrazier(x: number, z: number): void {
    this.instantiate("SM_PROP_brazier_dungeon_02", new Vector3(x, 0, z));
    const light = new PointLight(`brazier_${x}_${z}`, new Vector3(x, 1.4, z), this.scene);
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
    for (const monster of initial.monsters) {
      const root = new TransformNode(`monster_${monster.index}`, this.scene);
      const matching = this.content.monsters.find((m) => m.name === monster.name);
      const icon = monster.isBoss
        ? monsterIcon(this.content, this.content.monsters[0])
        : monsterIcon(this.content, matching ?? this.content.monsters[monster.index % this.content.monsters.length]);
      this.addToken(root, icon, monster.isBoss ? new Color3(0.95, 0.75, 0.2) : new Color3(0.9, 0.25, 0.2));
      if (monster.isBoss) root.scaling = new Vector3(1.6, 1.6, 1.6);
      this.monsterEntities.set(monster.index, {
        root,
        target: new Vector3(monster.x, 0, monster.z),
        lastHp: monster.hp,
        alive: monster.alive,
        moving: false,
        stepPhase: Math.random() * Math.PI * 2,
        level: monster.level,
      });
    }
  }

  // ------------------------------------------------------------------ input

  private bindInput(): void {
    this.scene.onPointerObservable.add((info) => {
      if (info.type !== PointerEventTypes.POINTERDOWN) return;
      const event = info.event as PointerEvent;
      if (event.button !== 0) return;
      const pick = this.scene.pick(this.scene.pointerX, this.scene.pointerY, (mesh) => mesh === this.ground);
      if (pick?.hit && pick.pickedPoint) {
        this.hooks.moveTo(pick.pickedPoint.x, pick.pickedPoint.z);
        this.say("Moving");
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

    if (previous && previous.playerHp > state.playerHp) {
      this.addFloater(this.player.root.position, `-${Math.round(previous.playerHp - state.playerHp)}`, "#ff6b6b");
    }
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
      autoMove: this.autoMove,
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
