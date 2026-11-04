using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Eldenheim.Sim;

// Differential test: replays the sandbox's demo (recorded by tools/simtrace
// from the Rust sim) through the C# port and compares every tick.
//
//   dotnet run --project tests/Eldenheim.Sim.Replay -- build/eldenheim_data.bin build/demo_trace.bin

string dataPath = args.Length > 0 ? args[0] : "build/eldenheim_data.bin";
string tracePath = args.Length > 1 ? args[1] : "build/demo_trace.bin";
ErData.Load(dataPath);

using var r = new BinaryReader(File.OpenRead(tracePath));
if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "EHTR" || r.ReadUInt32() != 1)
{
    throw new IOException("bad trace");
}

var world = new World(BlockLevel.Arena());
Snap prevAfter = null;
string caption = "";
int ticks = 0, bad = 0, firstBad = -1, syncBad = 0;
var mirror = new Player(new Vec3(0f, 0f, 0f));
var reports = new List<string>();
var seen = new HashSet<string>();
var worst = new Dictionary<string, float>();

while (r.ReadByte() == 1)
{
    if (r.ReadByte() == 1)
    {
        caption = Str(r);
    }
    Snap before = Snap.Read(r);
    Input inp = ReadInput(r);
    Snap after = Snap.Read(r);
    int logs = (int)r.ReadUInt32();
    for (int i = 0; i < logs; i++)
    {
        Str(r);
    }

    // The director edits the world between ticks (refill HP, equip a pair,
    // turn the dummy hostile...): copy exactly those edits across.
    Snap reference = prevAfter;
    if (reference == null || before.Hp != reference.Hp) world.Player.Hp = before.Hp;
    if (reference == null || before.Stamina != reference.Stamina) world.Player.Stamina = before.Stamina;
    if (reference == null || before.Weapon != reference.Weapon) world.Player.Weapon = before.Weapon;
    if (reference == null || before.Left != reference.Left) world.Player.Left = before.Left;
    if (reference == null || before.Load != reference.Load) world.Player.Load = (Load)before.Load;
    if (reference == null || before.DHp != reference.DHp) world.Dummy.Hp = before.DHp;
    if (reference == null || before.Aggressive != reference.Aggressive) world.Dummy.Aggressive = before.Aggressive;

    world.Step(inp);
    world.Log.Clear();
    Snap mine = Snap.Of(world);
    List<string> diffs = mine.Compare(after, worst);
    if (diffs.Count > 0)
    {
        bad++;
        if (firstBad < 0)
        {
            firstBad = ticks;
        }
        if (reports.Count < 12)
        {
            reports.Add($"tick {ticks} [{caption}]: {string.Join("; ", diffs)}\n    rust {after}\n    c#   {mine}");
        }
    }
    // The network pose state round-trips every tick (PoseState, PLAN §3.7).
    byte[] sent = PoseState.Write(world.Player);
    if (!PoseState.Read(sent, mirror) || !System.Linq.Enumerable.SequenceEqual(PoseState.Write(mirror), sent))
    {
        syncBad++;
    }
    seen.Add(after.Name);
    prevAfter = after;
    ticks++;
}

Console.WriteLine($"{ticks} ticks ({ticks / 60f:0} s), {seen.Count} distinct states, {bad} ticks differ");
Console.WriteLine($"pose state: {syncBad} ticks fail the round trip");
foreach (var kv in worst)
{
    Console.WriteLine($"  worst {kv.Key}: {kv.Value:G4}");
}
foreach (string line in reports)
{
    Console.WriteLine(line);
}
return bad == 0 && syncBad == 0 ? 0 : 1;

static string Str(BinaryReader r) => Encoding.UTF8.GetString(r.ReadBytes((int)r.ReadUInt32()));

static Button Btn(byte b) => new Button { Held = (b & 1) != 0, Pressed = (b & 2) != 0, Released = (b & 4) != 0 };

static Input ReadInput(BinaryReader r)
{
    var i = new Input { Mv = new Vec2(r.ReadSingle(), r.ReadSingle()), CamYaw = r.ReadSingle() };
    i.Dodge = Btn(r.ReadByte());
    i.Jump = Btn(r.ReadByte());
    i.Light = Btn(r.ReadByte());
    i.Heavy = Btn(r.ReadByte());
    i.Guard = Btn(r.ReadByte());
    byte f = r.ReadByte();
    i.Crouch = (f & 1) != 0;
    i.Lock = (f & 2) != 0;
    i.Walk = (f & 4) != 0;
    i.TwoHandRight = (f & 8) != 0;
    i.TwoHandLeft = (f & 16) != 0;
    i.NextWeapon = (f & 32) != 0;
    i.NextLeft = (f & 64) != 0;
    return i;
}

class Snap
{
    public float X, Y, Z, Yaw, Hp, Stamina, Speed, HitStop, F, AirF, SwapF;
    public byte Kind, Flags, Grip, Weapon, Left, Load, Buffer;
    public byte[] Key = new byte[4];
    public float DX, DY, DZ, DYaw, DHp, DPoise, DT, DHitStop;
    public byte DState;
    public bool Aggressive;

    public string Name => Kind == 1 ? new ActionId((ActionTag)Key[0], Key[1], Key[2], Key[3]).ToString() : new[] { "Ground", "Act", "Air", "Dead" }[Kind];

    public static Snap Read(BinaryReader r)
    {
        var s = new Snap
        {
            X = r.ReadSingle(), Y = r.ReadSingle(), Z = r.ReadSingle(), Yaw = r.ReadSingle(), Hp = r.ReadSingle(),
            Stamina = r.ReadSingle(), Speed = r.ReadSingle(), HitStop = r.ReadSingle(),
        };
        s.Kind = r.ReadByte();
        s.Key = r.ReadBytes(4);
        s.F = r.ReadSingle();
        s.Flags = r.ReadByte();
        s.Grip = r.ReadByte();
        s.Weapon = r.ReadByte();
        s.Left = r.ReadByte();
        s.Load = r.ReadByte();
        s.Buffer = r.ReadByte();
        s.AirF = r.ReadSingle();
        s.SwapF = r.ReadSingle();
        s.DX = r.ReadSingle(); s.DY = r.ReadSingle(); s.DZ = r.ReadSingle(); s.DYaw = r.ReadSingle();
        s.DHp = r.ReadSingle(); s.DPoise = r.ReadSingle(); s.DT = r.ReadSingle(); s.DHitStop = r.ReadSingle();
        s.DState = r.ReadByte();
        s.Aggressive = r.ReadByte() != 0;
        return s;
    }

    public static Snap Of(World w)
    {
        Player p = w.Player;
        var s = new Snap
        {
            X = p.Pos.X, Y = p.Pos.Y, Z = p.Pos.Z, Yaw = p.Yaw, Hp = p.Hp, Stamina = p.Stamina, Speed = p.Speed, HitStop = p.HitStop,
            Kind = (byte)p.State, Grip = (byte)p.Grip, Weapon = (byte)p.Weapon, Left = (byte)p.Left, Load = (byte)p.Load,
            Buffer = (byte)(p.Buffer.HasValue ? 1 + (int)p.Buffer.Value : 0),
            AirF = p.AirAttackState?.F ?? 0f, SwapF = p.SwapState?.F ?? 0f,
        };
        switch (p.State)
        {
            case StateKind.Act:
                s.Key = new[] { (byte)p.Act.Id.Tag, p.Act.Id.A, p.Act.Id.B, p.Act.Id.C };
                s.F = p.Act.F;
                break;
            case StateKind.Air:
                s.Key = new byte[] { (byte)(p.Air.Jumped ? 1 : 0), 0, 0, 0 };
                s.F = p.Air.F;
                break;
            case StateKind.Dead:
                s.F = p.DeadT;
                break;
        }
        bool[] flags = { p.Sprinting, p.Crouching, p.Guarding, w.Locked, p.AirAttackState.HasValue, p.SwapState.HasValue };
        for (int i = 0; i < flags.Length; i++)
        {
            s.Flags |= (byte)((flags[i] ? 1 : 0) << i);
        }
        Dummy d = w.Dummy;
        s.DX = d.Pos.X; s.DY = d.Pos.Y; s.DZ = d.Pos.Z; s.DYaw = d.Yaw; s.DHp = d.Hp; s.DPoise = d.Poise; s.DT = d.T; s.DHitStop = d.HitStop;
        s.DState = (byte)((d.State == global::Eldenheim.Sim.DState.Windup || d.State == global::Eldenheim.Sim.DState.Strike) && d.Low
            ? (byte)d.State + 10 : (byte)d.State);
        s.Aggressive = d.Aggressive;
        return s;
    }

    public List<string> Compare(Snap o, Dictionary<string, float> worst)
    {
        var d = new List<string>();
        void Num(string name, float a, float b, float tol)
        {
            float e = Math.Abs(a - b);
            if (!worst.TryGetValue(name, out float w) || e > w)
            {
                worst[name] = e;
            }
            if (e > tol)
            {
                d.Add($"{name} {a} vs {b}");
            }
        }
        void Exact(string name, object a, object b)
        {
            if (!a.Equals(b))
            {
                d.Add($"{name} {a} vs {b}");
            }
        }
        Num("pos", Math.Max(Math.Abs(X - o.X), Math.Max(Math.Abs(Y - o.Y), Math.Abs(Z - o.Z))), 0f, 1e-3f);
        Num("yaw", SimMath.AngleDiff(Yaw, o.Yaw), 0f, 1e-3f);
        Num("hp", Hp, o.Hp, 1e-2f);
        Num("stamina", Stamina, o.Stamina, 1e-2f);
        Num("speed", Speed, o.Speed, 1e-3f);
        Num("frame", F, o.F, 1e-3f);
        Num("hitstop", HitStop, o.HitStop, 1e-4f);
        Exact("state", Name, o.Name);
        Exact("flags", Flags, o.Flags);
        Exact("grip", Grip, o.Grip);
        Exact("weapon", Weapon, o.Weapon);
        Exact("left", Left, o.Left);
        Exact("buffer", Buffer, o.Buffer);
        Num("dummy pos", Math.Max(Math.Abs(DX - o.DX), Math.Abs(DZ - o.DZ)), 0f, 1e-3f);
        Num("dummy yaw", SimMath.AngleDiff(DYaw, o.DYaw), 0f, 1e-3f);
        Num("dummy hp", DHp, o.DHp, 1e-2f);
        Exact("dummy state", DState, o.DState);
        return d;
    }

    public override string ToString() =>
        $"{Name} f={F:0.00} pos=({X:0.000},{Y:0.000},{Z:0.000}) yaw={Yaw:0.000} hp={Hp:0.0} st={Stamina:0.00} spd={Speed:0.000} flags={Flags} buf={Buffer} dummy={DState} dhp={DHp:0}";
}
