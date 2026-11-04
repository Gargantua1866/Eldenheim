using Eldenheim.Anim;
using Eldenheim.Sim;
using HarmonyLib;
using UnityEngine;
using SimPlayer = Eldenheim.Sim.Player;
using VPlayer = global::Player;

namespace Eldenheim
{
    /// <summary>
    /// Valheim's movement sounds and dust, driven by the ER animation.
    /// Valheim's FootStep fires on its own clips' "footstep" curve while the
    /// animator's speed parameters are up; under Eldenheim those parameters
    /// are no longer set, so vanilla steps either never fire or (if they were
    /// up when Eldenheim took over) repeat forever. Here they are held at
    /// zero, and a step is played whenever a foot comes down in the ER clip,
    /// with Valheim's walk / jog / run (dust) choice. Jumps, rolls and
    /// landings get Valheim's own jump, dodge and land effects.
    /// </summary>
    public class Steps
    {
        private const float LIFT = 0.06f, PLANT = 0.025f;
        private static readonly int ForwardSpeed = ZSyncAnimation.GetHash("forward_speed");
        private static readonly int SidewaySpeed = ZSyncAnimation.GetHash("sideway_speed");
        private static readonly System.Reflection.MethodInfo OnFoot = AccessTools.Method(typeof(FootStep), "OnFoot", new[] { typeof(Transform) });
        private static readonly AccessTools.FieldRef<Character, bool> Running = AccessTools.FieldRefAccess<Character, bool>("m_running");
        private static readonly AccessTools.FieldRef<Character, bool> Walking = AccessTools.FieldRefAccess<Character, bool>("m_walking");

        private readonly VPlayer _vp;
        private readonly FootStep _footStep;
        private readonly ZSyncAnimation _zanim;
        private readonly Transform[] _feet = new Transform[2];
        private readonly int[] _erFeet;
        private readonly float[] _rest = new float[2];
        private readonly bool[] _lifted = new bool[2];

        private StateKind _lastState;
        private ActionId _lastAct;
        private float _lastF;

        public string Last = "";

        public Steps(VPlayer vp, Clips clips)
        {
            _vp = vp;
            _footStep = vp.GetComponent<FootStep>();
            _zanim = Traverse.Create(vp).Field("m_zanim").GetValue<ZSyncAnimation>();
            _erFeet = new[] { clips.Bone("L_Foot"), clips.Bone("R_Foot") };
            for (int s = 0; s < 2; s++)
            {
                _rest[s] = _erFeet[s] >= 0 ? clips.Rest.Pos[_erFeet[s]].y : 0f;
            }
            if (_footStep != null)
            {
                foreach (Transform t in _footStep.m_feet)
                {
                    if (t == null)
                    {
                        continue;
                    }
                    _feet[t.name.Contains("Left") || t.name.StartsWith("L") ? 0 : 1] = t;
                }
            }
        }

        /// <summary>Keeps vanilla's footstep curve silent (its speed gate shut).</summary>
        public void Silence()
        {
            if (_zanim != null)
            {
                _zanim.SetFloat(ForwardSpeed, 0f);
                _zanim.SetFloat(SidewaySpeed, 0f);
            }
        }

        public enum Started { None, Jump, Dodge }

        /// <summary>
        /// After each sim tick: jump and roll effects. Returns which of them
        /// began this tick (the caller charges Valheim's stamina for it).
        /// Landings are Valheim's own (Character.UpdateGroundContact plays
        /// them from the fall height).
        /// </summary>
        public Started AfterTick(SimPlayer p)
        {
            Started what = Started.None;
            bool acting = p.State == StateKind.Act;
            bool started = acting && (_lastState != StateKind.Act || !p.Act.Id.Equals(_lastAct) || p.Act.F < _lastF);
            if (started)
            {
                switch (p.Act.Id.Tag)
                {
                    case ActionTag.Jump:
                        _vp.m_jumpEffects.Create(_vp.transform.position, _vp.transform.rotation, _vp.transform, 1f, -1, _vp.GetZDOID());
                        Last = "jump";
                        what = Started.Jump;
                        break;
                    case ActionTag.Roll:
                    case ActionTag.CrouchRoll:
                    case ActionTag.Backstep:
                        _vp.m_dodgeEffects.Create(_vp.transform.position, Quaternion.identity, _vp.transform, 1f, -1, _vp.GetZDOID());
                        Last = "dodge";
                        what = Started.Dodge;
                        break;
                }
            }
            _lastState = p.State;
            if (acting)
            {
                _lastAct = p.Act.Id;
                _lastF = p.Act.F;
            }
            return what;
        }

        /// <summary>Each physics step, after the pose: a step for each foot the clip puts down.</summary>
        public void Feet(SimPlayer p, ErPose shown, float speed)
        {
            bool planted = !p.Airborne() && p.State != StateKind.Air && p.State != StateKind.Dead;
            for (int s = 0; s < 2; s++)
            {
                if (_erFeet[s] < 0)
                {
                    continue;
                }
                float h = shown.Pos[_erFeet[s]].y - _rest[s];
                if (h > LIFT)
                {
                    _lifted[s] = true;
                }
                else if (_lifted[s] && h < PLANT)
                {
                    _lifted[s] = false;
                    if (planted && speed > 0.3f)
                    {
                        Step(p, s, speed);
                    }
                }
            }
        }

        private void Step(SimPlayer p, int side, float speed)
        {
            if (_footStep == null || OnFoot == null)
            {
                return;
            }
            // Valheim picks walk / jog / run (with dust) from these two flags.
            bool running = Running(_vp), walking = Walking(_vp);
            bool walk = !p.Sprinting && speed <= ErData.WALK_SPEED + 0.3f;
            Running(_vp) = p.Sprinting;
            Walking(_vp) = walk;
            Last = p.Sprinting ? "run step" : walk ? "walk step" : "step";
            try
            {
                OnFoot.Invoke(_footStep, new object[] { _feet[side] != null ? _feet[side] : _vp.transform });
            }
            finally
            {
                Running(_vp) = running;
                Walking(_vp) = walking;
            }
        }
    }
}
