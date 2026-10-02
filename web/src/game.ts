// M0 Babylon scene: the exported Unity dungeon kit + billboarded avatar tokens, top-down
// camera, click-to-move, auto-attack, floating damage numbers. This mirrors the original's
// presentation (3D environment, circular face tokens) as observed on the store page.

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

import { Snapshot } from "./api";
import { ContentManifest, monsterIcon, raceIcon } from "./content";
import { experienceForLevel, levelForExperience, rollDamage } from "./combat";

export interface HudState {
  health: number;
  maxHealth: number;
  level: number;
  experience: number;
  experienceForLevel: number;
  experienceForNextLevel: number;
  silver: number;
  opals: number;
  monsterKills: number;
  monstersAlive: number;
  autoMove: boolean;
  message: string;
}

interface Entity {
  root: TransformNode;
  hp: number;
  maxHp: number;
  offense: number;
  defense: number;
  level: number;
  speed: number;
  attackInterval: number;
  attackTimer: number;
  alive: boolean;
  kind: "player" | "monster";
  wanderTimer: number;
  wanderTarget: Vector3 | null;
  moving: boolean;
  stepPhase: number;
}

interface Floater {
  el: HTMLDivElement;
  world: Vector3;
  born: number;
  ttl: number;
}

const KIT = "/assets/kit/dungeon_default/";
const ROOM_HALF = 18;
const FLOOR_SPACING = 6;
const PLAYER_ATTACK_RANGE = 3.6;
const MONSTER_ATTACK_RANGE = 2.1;

export class World {
  private readonly engine: Engine;
  private readonly scene: Scene;
  private readonly camera: ArcRotateCamera;
  private readonly canvas: HTMLCanvasElement;
  private readonly overlay: HTMLDivElement;
  private readonly content: ContentManifest;
  private readonly onHud: (hud: HudState) => void;
  private readonly templates = new Map<string, TransformNode>();
  private ground: Nullable<Mesh> = null;
  private player!: Entity;
  private monsters: Entity[] = [];
  private floaters: Floater[] = [];
  private playerTarget: Vector3 | null = null;
  private elapsed = 0;
  private hudTimer = 0;
  private autoMove = true;
  private silver = 0;
  private opals = 0;
  private monsterKills = 0;
  private message = "";
  private messageUntil = 0;
  private hudDirty = true;
  private disposed = false;

  constructor(
    canvas: HTMLCanvasElement,
    overlay: HTMLDivElement,
    snapshot: Snapshot,
    content: ContentManifest,
    onHud: (hud: HudState) => void,
  ) {
    this.canvas = canvas;
    this.overlay = overlay;
    this.content = content;
    this.onHud = onHud;
    this.silver = snapshot.silver;
    this.opals = snapshot.opals;
    this.monsterKills = snapshot.monsterKills;

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
    // Lock the yaw so the camera stays behind the player like the original top-down view.
    this.camera.inputs.removeByType("ArcRotateCameraPointersInput");

    const ambient = new HemisphericLight("ambient", new Vector3(0.3, 1, 0.1), this.scene);
    ambient.intensity = 0.95;
    ambient.diffuse = new Color3(0.85, 0.82, 0.75);
    ambient.groundColor = new Color3(0.18, 0.16, 0.14);

    this.onHud({
      health: snapshot.level * 100,
      maxHealth: snapshot.level * 100,
      level: snapshot.level,
      experience: snapshot.experienceForLevel,
      experienceForLevel: snapshot.experienceForLevel,
      experienceForNextLevel: snapshot.experienceForNextLevel,
      silver: this.silver,
      opals: this.opals,
      monsterKills: this.monsterKills,
      monstersAlive: 0,
      autoMove: this.autoMove,
      message: "Click the floor to move. Auto-move is on.",
    });
  }

  async start(snapshot: Snapshot): Promise<void> {
    await this.loadKit();
    this.buildRoom();
    this.buildPlayer(snapshot);
    this.spawnMonsters(snapshot.level);
    this.bindInput();
    this.scene.onBeforeRenderObservable.add(() => this.update(this.engine.getDeltaTime() / 1000));

    this.engine.runRenderLoop(() => {
      if (!this.disposed) this.scene.render();
    });
    window.addEventListener("resize", () => this.engine.resize());
    this.engine.resize();
  }

  toggleAutoMove(): boolean {
    this.autoMove = !this.autoMove;
    this.say(this.autoMove ? "Auto-move on" : "Auto-move off");
    return this.autoMove;
  }

  useSkill(): void {
    const target = this.nearestMonster(8);
    if (!target) {
      this.say("No target in range");
      return;
    }
    const { damage, critical } = rollDamage(this.player.offense, target.defense, this.player.level, {
      skillMultiplier: 2.4,
      critChance: 0.15,
      critMultiplier: 2.0,
    });
    this.applyDamage(target, damage, critical);
    // Visual: a quick ring flash from the player.
    this.flashRing(this.player.root.position, new Color3(0.4, 0.7, 1));
    this.say("Skill cast");
  }

  dispose(): void {
    this.disposed = true;
    this.engine.stopRenderLoop();
    this.scene.dispose();
    this.engine.dispose();
    this.overlay.querySelectorAll(".floater").forEach((el) => el.remove());
    window.removeEventListener("resize", this.resize);
  }

  private resize = (): void => this.engine.resize();

  // ------------------------------------------------------------------ content

  private async loadKit(): Promise<void> {
    const names = [
      "Floor_Slab_lrg",
      "MOD_Wall_01_O_straight_large",
      "MOD_Column_01_large",
      "SM_PROP_brazier_dungeon_02",
      "SM_PROP_torch_standing_dungeon",
      "SM_PROP_planks_dungeon_05",
    ];
    await Promise.all(
      names.map(async (name) => {
        try {
          const result = await SceneLoader.ImportMeshAsync("", KIT, `${name}.glb`, this.scene);
          const root = result.meshes[0] as TransformNode;
          result.meshes.forEach((mesh) => {
            mesh.isPickable = false;
            if (mesh.material) {
              const mat = mesh.material as StandardMaterial;
              mat.specularColor = new Color3(0.05, 0.05, 0.05);
              if (mat.diffuseTexture) (mat.diffuseTexture as Texture).uScale = 1;
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
    clone.scaling = new Vector3(scale, scale, scale);
    clone.getChildMeshes().forEach((mesh) => {
      mesh.isPickable = false;
      mesh.receiveShadows = true;
    });
    return clone;
  }

  private buildRoom(): void {
    const groundMat = new StandardMaterial("groundMat", this.scene);
    groundMat.diffuseColor = new Color3(0.22, 0.19, 0.16);
    groundMat.specularColor = new Color3(0, 0, 0);
    groundMat.ambientColor = new Color3(0.3, 0.28, 0.25);
    const ground = MeshBuilder.CreateGround("ground", { width: 2 * ROOM_HALF + 6, height: 2 * ROOM_HALF + 6 }, this.scene);
    ground.material = groundMat;
    ground.receiveShadows = true;
    ground.isPickable = true;
    this.ground = ground;

    const count = Math.floor((2 * ROOM_HALF) / FLOOR_SPACING) + 1;
    for (let ix = 0; ix < count; ix++) {
      for (let iz = 0; iz < count; iz++) {
        const x = -ROOM_HALF + ix * FLOOR_SPACING;
        const z = -ROOM_HALF + iz * FLOOR_SPACING;
        this.instantiate("Floor_Slab_lrg", new Vector3(x, 0.02, z));
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

    const corners: [number, number][] = [
      [-ROOM_HALF, -ROOM_HALF],
      [ROOM_HALF, -ROOM_HALF],
      [-ROOM_HALF, ROOM_HALF],
      [ROOM_HALF, ROOM_HALF],
    ];
    for (const [x, z] of corners) {
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

  private makeTokenTexture(url: string): StandardMaterial {
    const material = new StandardMaterial(`token_${url}`, this.scene);
    material.diffuseTexture = new Texture(url, this.scene);
    material.diffuseTexture.hasAlpha = true;
    material.useAlphaFromDiffuseTexture = true;
    material.specularColor = new Color3(0, 0, 0);
    material.emissiveColor = new Color3(0.35, 0.35, 0.35);
    material.backFaceCulling = false;
    return material;
  }

  private buildToken(root: TransformNode, textureUrl: string, ringColor: Color3): void {
    const disc = MeshBuilder.CreateCylinder(`${root.name}_disc`, { diameter: 2.1, height: 0.08, tessellation: 24 }, this.scene);
    disc.parent = root;
    disc.position.y = 0.05;
    disc.isPickable = false;
    const discMat = new StandardMaterial(`${root.name}_discMat`, this.scene);
    discMat.diffuseColor = new Color3(0.05, 0.05, 0.06);
    discMat.emissiveColor = new Color3(0.02, 0.02, 0.02);
    disc.material = discMat;

    const ring = MeshBuilder.CreateTorus(`${root.name}_ring`, { diameter: 2.5, thickness: 0.14, tessellation: 32 }, this.scene);
    ring.parent = root;
    ring.position.y = 0.12;
    ring.isPickable = false;
    const ringMat = new StandardMaterial(`${root.name}_ringMat`, this.scene);
    ringMat.emissiveColor = ringColor;
    ringMat.diffuseColor = ringColor;
    ring.material = ringMat;

    const face = MeshBuilder.CreatePlane(`${root.name}_face`, { size: 1.9 }, this.scene);
    face.parent = root;
    face.rotation.x = Math.PI / 2;
    face.position.y = 0.14;
    face.isPickable = false;
    face.material = this.makeTokenTexture(textureUrl);
    face.billboardMode = 0;
  }

  private buildPlayer(snapshot: Snapshot): void {
    const root = new TransformNode("player", this.scene);
    root.position = new Vector3(0, 0, 0);
    const maxHp = 120 + snapshot.level * 45;
    this.player = {
      root,
      hp: maxHp,
      maxHp,
      offense: Math.max(10, snapshot.offense),
      defense: Math.max(0, snapshot.defense),
      level: snapshot.level,
      speed: 6.5,
      attackInterval: 0.75,
      attackTimer: 0,
      alive: true,
      kind: "player",
      wanderTimer: 0,
      wanderTarget: null,
      moving: false,
      stepPhase: 0,
    };
    this.buildToken(root, raceIcon(this.content, snapshot.race), new Color3(0.35, 0.8, 1));
  }

  private spawnMonsters(playerLevel: number): void {
    const pool = this.content.monsters.filter((m) => m.name);
    const count = 7;
    for (let i = 0; i < count; i++) {
      const monster = pool[(i * 17 + 3) % pool.length];
      const angle = (i / count) * Math.PI * 2;
      const radius = 7 + (i % 3) * 3;
      const root = new TransformNode(`monster_${i}`, this.scene);
      root.position = new Vector3(Math.cos(angle) * radius, 0, Math.sin(angle) * radius);
      const level = Math.max(1, playerLevel + (i % 3) - 1);
      const maxHp = 40 + level * 22;
      this.buildToken(root, monsterIcon(this.content, monster), new Color3(0.9, 0.25, 0.2));
      this.monsters.push({
        root,
        hp: maxHp,
        maxHp,
        offense: 6 + level * 3,
        defense: 2 + level * 1.5,
        level,
        speed: 2.4,
        attackInterval: 1.4,
        attackTimer: 0,
        alive: true,
        kind: "monster",
        wanderTimer: Math.random() * 2,
        wanderTarget: null,
        moving: false,
        stepPhase: Math.random() * Math.PI * 2,
      });
    }
    this.hudDirty = true;
  }

  // ------------------------------------------------------------------ input

  private bindInput(): void {
    this.scene.onPointerObservable.add((info) => {
      if (info.type !== PointerEventTypes.POINTERDOWN) return;
      const event = info.event as PointerEvent;
      if (event.button !== 0) return;
      const pick = this.scene.pick(this.scene.pointerX, this.scene.pointerY, (mesh) => mesh === this.ground);
      if (pick?.hit && pick.pickedPoint) {
        this.playerTarget = pick.pickedPoint.clone();
        this.playerTarget.y = 0;
        this.say("Moving");
      }
    });
  }

  // ------------------------------------------------------------------ update

  private update(dt: number): void {
    if (dt <= 0) return;
    this.elapsed += dt;
    this.updatePlayer(dt);
    this.updateMonsters(dt);
    this.updateFloaters();
    this.hudTimer += dt;
    if (this.hudTimer > 0.2 || this.hudDirty) {
      this.hudTimer = 0;
      this.hudDirty = false;
      this.emitHud();
    }
  }

  private updatePlayer(dt: number): void {
    if (!this.player.alive) return;
    this.player.attackTimer -= dt;

    // Auto-move: head to the nearest monster when idle; otherwise follow the click target.
    let destination = this.playerTarget;
    const nearest = this.nearestMonster(PLAYER_ATTACK_RANGE);
    let chasing: Entity | null = null;
    if (!destination && this.autoMove) {
      const far = this.nearestMonster(30);
      if (far) {
        chasing = far;
        destination = far.root.position;
      }
    }

    if (nearest && nearest.root.position.subtract(this.player.root.position).length() <= PLAYER_ATTACK_RANGE) {
      // In range: stop and attack.
      chaseStep(this.player, dt, null);
      if (this.player.attackTimer <= 0) {
        this.player.attackTimer = this.player.attackInterval;
        const { damage, critical } = rollDamage(this.player.offense, nearest.defense, this.player.level);
        this.applyDamage(nearest, damage, critical);
      }
    } else if (destination) {
      const arrived = chaseStep(this.player, dt, destination);
      if (arrived && destination === this.playerTarget) this.playerTarget = null;
      void chasing;
    }
    this.trackToken(this.player, dt);
  }

  private updateMonsters(dt: number): void {
    for (const monster of this.monsters) {
      if (!monster.alive) continue;
      monster.attackTimer -= dt;
      const toPlayer = this.player.root.position.subtract(monster.root.position);
      const distance = toPlayer.length();
      if (distance <= MONSTER_ATTACK_RANGE && this.player.alive) {
        chaseStep(monster, dt, null);
        if (monster.attackTimer <= 0) {
          monster.attackTimer = monster.attackInterval;
          const { damage, critical } = rollDamage(monster.offense, this.player.defense, monster.level, {
            critChance: 0.03,
          });
          const actual = Math.min(this.player.hp, Math.round(damage));
          this.player.hp -= actual;
          this.addFloater(this.player.root.position, `-${actual}`, critical ? "#ffd24a" : "#ff6b6b");
          if (this.player.hp <= 0) this.onPlayerDeath();
        }
        continue;
      }
      if (this.autoMove && this.player.alive) {
        chaseStep(monster, dt, this.player.root.position);
      } else {
        monster.wanderTimer -= dt;
        if (monster.wanderTimer <= 0 || !monster.wanderTarget) {
          monster.wanderTimer = 2 + Math.random() * 3;
          const angle = Math.random() * Math.PI * 2;
          const radius = 4 + Math.random() * 9;
          monster.wanderTarget = new Vector3(Math.cos(angle) * radius, 0, Math.sin(angle) * radius);
        }
        if (monster.wanderTarget && chaseStep(monster, dt, monster.wanderTarget)) monster.wanderTarget = null;
      }
      this.trackToken(monster, dt);
    }
    this.monsters = this.monsters.filter((m) => m.alive);
  }

  private trackToken(entity: Entity, dt: number): void {
    if (entity.moving) {
      entity.stepPhase += dt * 10;
      entity.root.position.y = Math.abs(Math.sin(entity.stepPhase)) * 0.12;
    } else {
      entity.root.position.y = 0;
    }
  }

  private nearestMonster(range: number): Entity | null {
    let best: Entity | null = null;
    let bestDistance = range;
    for (const monster of this.monsters) {
      if (!monster.alive) continue;
      const distance = Vector3.Distance(monster.root.position, this.player.root.position);
      if (distance < bestDistance) {
        bestDistance = distance;
        best = monster;
      }
    }
    return best;
  }

  private applyDamage(target: Entity, damage: number, critical: boolean): void {
    const actual = Math.min(target.hp, Math.round(damage));
    target.hp -= actual;
    this.addFloater(target.root.position, `-${actual}`, critical ? "#ffd24a" : "#ffe9c7");
    if (target.hp <= 0) {
      target.alive = false;
      target.root.setEnabled(false);
      this.monsterKills++;
      this.gainExperience(target.level);
      this.say(`Defeated a level ${target.level} monster`);
    }
    this.hudDirty = true;
  }

  private gainExperience(monsterLevel: number): void {
    const gain = Math.round(8 * Math.pow(monsterLevel, 1.35) + 5);
    let experience = this.currentExperience + gain;
    let level = levelForExperience(experience);
    if (level > this.player.level) {
      this.player.level = level;
      this.player.maxHp = 120 + level * 45;
      this.player.hp = this.player.maxHp;
      this.player.offense += 1.5;
      this.player.defense += 1;
      this.say(`Level up! Level ${level}`);
    }
    this.currentExperience = experience;
    this.hudDirty = true;
  }

  private currentExperience = 0;

  private onPlayerDeath(): void {
    this.player.alive = false;
    this.say("You died. Respawning...");
    window.setTimeout(() => {
      this.player.alive = true;
      this.player.hp = this.player.maxHp;
      this.player.root.position = new Vector3(0, 0, 0);
      this.hudDirty = true;
    }, 1500);
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
    const keep: Floater[] = [];
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

  private flashRing(position: Vector3, color: Color3): void {
    const ring = MeshBuilder.CreateTorus("skillring", { diameter: 1.5, thickness: 0.2, tessellation: 32 }, this.scene);
    ring.position = position.clone();
    ring.position.y = 0.2;
    const mat = new StandardMaterial("skillringMat", this.scene);
    mat.emissiveColor = color;
    ring.material = mat;
    const born = performance.now();
    const observer = this.scene.onBeforeRenderObservable.add(() => {
      const age = (performance.now() - born) / 500;
      if (age >= 1) {
        this.scene.onBeforeRenderObservable.remove(observer);
        ring.dispose();
        return;
      }
      const scale = 1 + age * 4;
      ring.scaling = new Vector3(scale, scale, scale);
    });
  }

  private say(message: string): void {
    this.message = message;
    this.messageUntil = this.elapsed + 2.5;
    this.hudDirty = true;
  }

  private emitHud(): void {
    this.onHud({
      health: Math.max(0, Math.round(this.player.hp)),
      maxHealth: Math.round(this.player.maxHp),
      level: this.player.level,
      experience: this.currentExperience,
      experienceForLevel: experienceForLevel(Math.floor(this.player.level)),
      experienceForNextLevel: experienceForLevel(Math.floor(this.player.level) + 1),
      silver: this.silver,
      opals: this.opals,
      monsterKills: this.monsterKills,
      monstersAlive: this.monsters.filter((m) => m.alive).length,
      autoMove: this.autoMove,
      message: this.elapsed < this.messageUntil ? this.message : "",
    });
  }
}

function chaseStep(entity: Entity, dt: number, destination: Vector3 | null): boolean {
  if (!destination) {
    entity.moving = false;
    return true;
  }
  const delta = destination.subtract(entity.root.position);
  delta.y = 0;
  const distance = delta.length();
  if (distance < 0.15) {
    entity.moving = false;
    return true;
  }
  const step = Math.min(distance, entity.speed * dt);
  entity.root.position.addInPlace(delta.normalize().scale(step));
  entity.moving = true;
  // Face the movement direction.
  entity.root.rotation.y = Math.atan2(delta.x, delta.z);
  return false;
}
