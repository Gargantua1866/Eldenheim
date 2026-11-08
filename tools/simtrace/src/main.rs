//! Exports the sandbox's extracted data and reference traces for Eldenheim.
//!
//!     simtrace data  OUT.bin      every action, weapon, air attack and swap
//!     simtrace demo  OUT.bin      the demo tour, played headless: per tick the
//!                                 input given and the state that resulted
//!
//! The sim and the demo are the sandbox's own source files (sandbox/, see
//! build note in Cargo.toml); nothing here reimplements them.
#![allow(dead_code)]

use std::fs::File;
use std::io::{BufWriter, Write};

use bevy::prelude::Resource;

#[path = "../../../sandbox/src/sim/mod.rs"]
mod sim;
#[path = "../../../sandbox/src/demo.rs"]
mod demo;

/// What demo.rs needs of the app around it.
mod camera {
    #[derive(bevy::prelude::Resource, Default)]
    pub struct CamRig {
        pub yaw: f32,
        pub demo: Option<f32>,
    }
}
#[derive(Resource)]
pub struct Sim(pub sim::World);
#[derive(Resource, Default)]
pub struct Pending(pub sim::player::Input);
#[derive(Resource)]
pub struct Options {
    pub show_iframes: bool,
    pub show_help: bool,
}

use sim::data::*;
use sim::extracted;
use sim::player::{Button, Input, State};

struct Out(BufWriter<File>);

impl Out {
    fn u8(&mut self, v: u8) {
        self.0.write_all(&[v]).unwrap();
    }
    fn u32(&mut self, v: u32) {
        self.0.write_all(&v.to_le_bytes()).unwrap();
    }
    fn f32(&mut self, v: f32) {
        self.0.write_all(&v.to_le_bytes()).unwrap();
    }
    fn str(&mut self, s: &str) {
        self.u32(s.len() as u32);
        self.0.write_all(s.as_bytes()).unwrap();
    }
    fn blade(&mut self, blade: &[[f32; 6]]) {
        self.u32(blade.len() as u32);
        for row in blade {
            for &v in row {
                self.f32(v);
            }
        }
    }
    fn action(&mut self, d: &ActionDef) {
        self.str(d.name);
        self.str(d.source);
        for v in [
            d.total, d.input_from, d.input_dodge_from, d.cancel_light, d.cancel_heavy, d.cancel_dodge, d.cancel_jump,
            d.cancel_guard, d.cancel_move, d.cancel_left, d.iframes.0, d.iframes.1,
        ] {
            self.f32(v);
        }
        self.u8(d.jump_frames as u8);
        self.f32(d.stamina);
        self.u32(d.hits.len() as u32);
        for h in d.hits {
            for v in [h.from, h.to, h.mv, h.guard_damage, h.stamina, h.stop, h.radius] {
                self.f32(v);
            }
            self.blade(h.blade);
            self.u8(h.throw as u8);
        }
        match d.charge {
            Some((a, b)) => {
                self.u8(1);
                self.f32(a);
                self.f32(b);
            }
            None => self.u8(0),
        }
        self.u32(d.no_turn.len() as u32);
        for &(a, b) in d.no_turn {
            self.f32(a);
            self.f32(b);
        }
        self.u32(d.turn.len() as u32);
        for &(a, b, c) in d.turn {
            self.f32(a);
            self.f32(b);
            self.f32(c);
        }
        self.u32(d.motion.len() as u32);
        for m in d.motion {
            for &v in m {
                self.f32(v);
            }
        }
        self.speed(d.speed);
    }
    fn speed(&mut self, windows: &[(f32, f32, f32, f32)]) {
        self.u32(windows.len() as u32);
        for &(a, b, c, e) in windows {
            for v in [a, b, c, e] {
                self.f32(v);
            }
        }
    }
    fn air(&mut self, d: &AirAttackDef) {
        self.f32(d.from);
        self.f32(d.to);
        self.f32(d.stamina);
        self.str(d.source);
        self.f32(d.radius);
        self.blade(d.blade);
        self.speed(d.speed);
    }
}

const LOADS: [Load; 3] = [Load::Light, Load::Medium, Load::Heavy];
const DIRS: [Dir; 4] = [Dir::Front, Dir::Back, Dir::Left, Dir::Right];
const LEVELS: [HurtLevel; 4] = [HurtLevel::Small, HurtLevel::Middle, HurtLevel::Large, HurtLevel::Knockdown];
const JUMPS: [JumpKind; 10] = [
    JumpKind::Stand, JumpKind::Walk, JumpKind::WalkBack, JumpKind::WalkLeft, JumpKind::WalkRight, JumpKind::Run,
    JumpKind::RunBack, JumpKind::RunLeft, JumpKind::RunRight, JumpKind::Sprint,
];
const SWAPS: [SwapKind; 6] = [
    SwapKind::ToTwoHandRight, SwapKind::ToTwoHandLeft, SwapKind::ToOneHandFromRight, SwapKind::ToOneHandFromLeft,
    SwapKind::NextWeapon, SwapKind::NextLeft,
];
const KINDS: [AttackKind; 39] = {
    use AttackKind::*;
    [
        Light1, Light2, Light3, Light4, Light5, Light6, RunLight, RunHeavy, RollAttack, CrouchAttack, BackstepAttack,
        Heavy1Charge, Heavy1, Heavy2Charge, Heavy2, GuardCounter, JumpLightLand, JumpLightLandShort, JumpHeavyLand,
        JumpHeavyLandShort, LeftLight1, LeftLight2, LeftLight3, LeftLight4, LeftLight5, LeftLight6, PairedLight1,
        PairedLight2, PairedLight3, PairedLight4, PairedLight5, PairedLight6, PairedRun, PairedRoll, PairedBackstep,
        PairedJumpLand, PairedJumpLandShort, Feint1, Feint2,
    ]
};

/// An ActionId as four bytes: tag, then up to three enum ordinals.
fn key(id: ActionId) -> [u8; 4] {
    match id {
        ActionId::Roll(l, d) => [0, l as u8, d as u8, 0],
        ActionId::CrouchRoll(l, d) => [1, l as u8, d as u8, 0],
        ActionId::Backstep => [2, 0, 0, 0],
        ActionId::SprintStop => [3, 0, 0, 0],
        ActionId::Jump(k) => [4, k as u8, 0, 0],
        ActionId::LandLight => [5, 0, 0, 0],
        ActionId::LandRun => [6, 0, 0, 0],
        ActionId::LandSprint => [7, 0, 0, 0],
        ActionId::LandStrafeWalk(d) => [8, d as u8, 0, 0],
        ActionId::LandStrafe(d) => [9, d as u8, 0, 0],
        ActionId::LandHeavy => [10, 0, 0, 0],
        ActionId::LandFall => [11, 0, 0, 0],
        ActionId::Attack(m, k) => [12, m.weapon, m.two_hand as u8, k as u8],
        ActionId::GuardHit => [13, 0, 0, 0],
        ActionId::GuardBreak => [14, 0, 0, 0],
        ActionId::Hurt(l, d) => [15, l as u8, d as u8, 0],
        ActionId::GuardBlow => [16, 0, 0, 0],
    }
}

fn all_base() -> Vec<ActionId> {
    let mut ids = vec![];
    for l in LOADS {
        for d in DIRS {
            ids.push(ActionId::Roll(l, d));
            ids.push(ActionId::CrouchRoll(l, d));
        }
    }
    ids.extend([ActionId::Backstep, ActionId::SprintStop]);
    ids.extend(JUMPS.map(ActionId::Jump));
    ids.extend([ActionId::LandLight, ActionId::LandRun, ActionId::LandSprint]);
    ids.extend(DIRS.map(ActionId::LandStrafeWalk));
    ids.extend(DIRS.map(ActionId::LandStrafe));
    ids.extend([ActionId::LandHeavy, ActionId::LandFall, ActionId::GuardHit, ActionId::GuardBreak, ActionId::GuardBlow]);
    for l in LEVELS {
        for d in DIRS {
            ids.push(ActionId::Hurt(l, d));
        }
    }
    ids
}

fn export_data(path: &str) {
    let mut o = Out(BufWriter::new(File::create(path).unwrap()));
    o.0.write_all(b"EHDT").unwrap();
    // 2: speed windows (TAE 608) after each action's motion and air attack's blade.
    // 3: each hit ends with a byte, 1 for a throw.
    o.u32(3);
    for v in [
        extracted::WALK_SPEED, extracted::RUN_SPEED, extracted::RUN_BACK_SPEED, extracted::RUN_SIDE_SPEED,
        extracted::SPRINT_SPEED, extracted::CROUCH_WALK_SPEED, extracted::CROUCH_RUN_SPEED, extracted::MAX_HP,
        extracted::MAX_STAMINA,
    ] {
        o.f32(v);
    }
    for v in [extracted::DEFAULT_WEAPON, extracted::SHIELD, extracted::FIST, extracted::TORCH] {
        o.u32(v as u32);
    }
    o.u32(WEAPONS.len() as u32);
    for w in WEAPONS {
        o.str(w.name);
        o.u8(w.category);
        o.f32(w.attack);
        o.f32(w.weight);
        o.u8(w.stance[0]);
        o.u8(w.stance[1]);
    }

    let mut actions: Vec<(ActionId, ActionDef)> = vec![];
    for id in all_base() {
        if let Some(d) = extracted::base(id) {
            actions.push((id, d));
        }
    }
    for w in 0..WEAPONS.len() {
        for two in [false, true] {
            for k in KINDS {
                if let Some(d) = extracted::attack(w, two, k) {
                    actions.push((ActionId::Attack(Moveset { weapon: w as u8, two_hand: two }, k), d));
                }
            }
        }
    }
    o.u32(actions.len() as u32);
    for (id, d) in &actions {
        for b in key(*id) {
            o.u8(b);
        }
        o.action(d);
    }

    let mut airs = vec![];
    for w in 0..WEAPONS.len() {
        for two in [false, true] {
            for heavy in [false, true] {
                if let Some(d) = extracted::air_attack(w, two, heavy) {
                    airs.push(([w as u8, two as u8, heavy as u8], d));
                }
            }
        }
        if let Some(d) = extracted::air_paired(w) {
            airs.push(([w as u8, 0, 2], d));
        }
    }
    o.u32(airs.len() as u32);
    for (k, d) in &airs {
        for b in k {
            o.u8(*b);
        }
        o.air(d);
    }

    for kind in SWAPS {
        let s = extracted::swap(kind);
        o.str(s.start);
        o.str(s.end);
        for v in [s.start_len, s.end_len, s.apply, s.free_from] {
            o.f32(v);
        }
    }
    o.0.flush().unwrap();
    eprintln!("data: {} actions, {} air attacks, {} weapons", actions.len(), airs.len(), WEAPONS.len());
}

fn button(o: &mut Out, b: Button) {
    o.u8(b.held as u8 | (b.pressed as u8) << 1 | (b.released as u8) << 2);
}

fn input(o: &mut Out, i: &Input) {
    o.f32(i.mv.x);
    o.f32(i.mv.y);
    o.f32(i.cam_yaw);
    for b in [i.dodge, i.jump, i.light, i.heavy, i.guard] {
        button(o, b);
    }
    let flags = [i.crouch, i.lock, i.walk, i.two_hand_right, i.two_hand_left, i.next_weapon, i.next_left];
    let mut bits = 0u8;
    for (n, f) in flags.iter().enumerate() {
        bits |= (*f as u8) << n;
    }
    o.u8(bits);
}

fn state(o: &mut Out, w: &sim::World) {
    let p = &w.player;
    for v in [p.pos.x, p.pos.y, p.pos.z, p.yaw, p.hp, p.stamina, p.speed, p.hit_stop] {
        o.f32(v);
    }
    match p.state {
        State::Ground => {
            o.u8(0);
            o.0.write_all(&[0; 4]).unwrap();
            o.f32(0.0);
        }
        State::Act(a) => {
            o.u8(1);
            o.0.write_all(&key(a.id)).unwrap();
            o.f32(a.f);
        }
        State::Air(a) => {
            o.u8(2);
            o.0.write_all(&[a.jumped as u8, 0, 0, 0]).unwrap();
            o.f32(a.f);
        }
        State::Dead { t } => {
            o.u8(3);
            o.0.write_all(&[0; 4]).unwrap();
            o.f32(t);
        }
    }
    let flags = [p.sprinting, p.crouching, p.guarding, w.locked, p.air_attack.is_some(), p.swap.is_some()];
    let mut bits = 0u8;
    for (n, f) in flags.iter().enumerate() {
        bits |= (*f as u8) << n;
    }
    o.u8(bits);
    o.u8(p.grip as u8);
    o.u8(p.weapon as u8);
    o.u8(p.left as u8);
    o.u8(p.load as u8);
    o.u8(match p.buffer {
        None => 0,
        Some(r) => 1 + r as u8,
    });
    o.f32(p.air_attack.map_or(0.0, |a| a.f));
    o.f32(p.swap.map_or(0.0, |s| s.f));
    let d = &w.dummy;
    for v in [d.pos.x, d.pos.y, d.pos.z, d.yaw, d.hp, d.poise, d.t, d.hit_stop] {
        o.f32(v);
    }
    o.u8(match d.state {
        sim::dummy::DState::Idle => 0,
        sim::dummy::DState::Windup { low } => 1 + low as u8 * 10,
        sim::dummy::DState::Strike { low } => 2 + low as u8 * 10,
        sim::dummy::DState::Recover => 3,
        sim::dummy::DState::Stagger => 4,
        sim::dummy::DState::Dead => 5,
    });
    o.u8(d.aggressive as u8);
}

fn export_demo(path: &str) {
    let mut o = Out(BufWriter::new(File::create(path).unwrap()));
    o.0.write_all(b"EHTR").unwrap();
    o.u32(1);
    let mut world = sim::World::new(sim::level::Level::arena());
    let mut director = demo::Director::new();
    let mut cam = 0.0_f32;
    let mut caption = "";
    let mut ticks = 0u32;
    // Same camera as the sandbox's own headless demo test.
    loop {
        cam = match world.target() {
            Some(target) => sim::yaw_of(target - world.player.pos),
            None => cam + sim::angle_diff(cam, director.cam) * 0.06,
        };
        // The director may change the world (reset the arena, turn the dummy
        // hostile...): record the state it hands to the step.
        let Some(inp) = director.drive(&mut world, cam) else {
            break;
        };
        o.u8(1);
        if director.caption != caption {
            caption = director.caption;
            o.u8(1);
            o.str(caption);
        } else {
            o.u8(0);
        }
        // The world before the step, so a replay can resync after director edits.
        state(&mut o, &world);
        input(&mut o, &inp);
        world.step(&inp);
        state(&mut o, &world);
        let log: Vec<String> = world.log.drain(..).collect();
        o.u32(log.len() as u32);
        for line in &log {
            o.str(line);
        }
        ticks += 1;
    }
    o.u8(0);
    o.0.flush().unwrap();
    eprintln!("demo: {ticks} ticks = {:.0} s", ticks as f32 / 60.0);
}

fn main() {
    let args: Vec<String> = std::env::args().collect();
    match (args.get(1).map(String::as_str), args.get(2)) {
        (Some("data"), Some(path)) => export_data(path),
        (Some("demo"), Some(path)) => export_demo(path),
        _ => eprintln!("usage: simtrace data|demo OUT.bin"),
    }
}
