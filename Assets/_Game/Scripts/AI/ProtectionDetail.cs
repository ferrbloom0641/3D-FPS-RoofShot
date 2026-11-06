using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace RooftopContract
{
    /// <summary>
    /// Runs the close-protection formation, in two modes.
    ///
    /// Before the first shot: if the target is going somewhere, the detail goes with him — a
    /// ring around him at walking pace, everyone facing outward. This used to not exist, and it
    /// showed badly the moment two of the three contracts got a target who walks: the escort
    /// stood at its spawn posts loitering while the man they were guarding strolled off across
    /// the venue alone. A detail that lets its principal walk away is not an escort.
    ///
    /// After the first shot: every guard gets an angular slot on the arc between the VIP and the
    /// shooter, at a radius that collapses from loose to body-contact. Dead guards are dropped
    /// and the arc re-solves, so shooting a bodyguard genuinely opens a hole — at the cost of
    /// one of the player's rounds.
    /// </summary>
    public class ProtectionDetail : MonoBehaviour
    {
        [Header("Detail")]
        public VipTarget vip;
        public List<ProtectorGuard> guards = new();

        [Header("Escort (첫 발 전)")]
        [Tooltip("켜면 첫 발 전에도 경호원이 VIP를 따라 이동한다. 걷는 표적(도로/헬리패드)용. " +
                 "정지한 표적은 끄는 게 맞다 — 회합장에서 경호원이 원을 그리고 도는 건 이상하다.")]
        public bool escortWhileCalm;
        [Tooltip("평시 호위 대형 반경 (진행 방향 기준). 넓을수록 '주변을 경계하며 간다'로 읽힌다.")]
        public float escortRadius = 3.4f;
        [Tooltip("좌우 폭 배율. 1 이면 정원, 낮추면 진행 방향으로 늘어난 타원이 된다. " +
                 "도로 미션의 인도는 폭이 7m뿐이라 정원이면 측면 경호원이 연석 밖으로 밀린다.")]
        [Range(0.3f, 1f)] public float escortLateralScale = 0.7f;
        [Tooltip("대형 전체를 진행 방향 기준으로 돌리는 각도 (도). 0이면 1번이 선두.")]
        public float escortFrontBias = 0f;

        [Header("Formation (첫 발 후)")]
        [Tooltip("경계 전 느슨한 반경")] public float calmRadius = 4f;
        [Tooltip("경계 후 밀착 반경")] public float shieldRadius = 1.0f;
        [Tooltip("위협 방향 기준 호(arc)의 전체 각도")] public float arcSpread = 50f;
        [Tooltip("대형이 완전히 좁혀지는 데 걸리는 시간 = 2번째 사격 기회 창")]
        public float shieldTightenTime = 1.5f;

        [Header("Arrival")]
        [Tooltip("켜져 있으면 경호대가 아직 차 안에 있다 (MotorcadeArrival 이 끈다). 그동안 대형을 짜지 않고, " +
                 "첫 착탄은 기억해 뒀다가 모두 내린 순간 경보로 처리한다.")]
        public bool holdForArrival;

        Vector3 threatPosition;
        bool alerted;
        bool pendingAlert;
        Vector3 pendingThreat;
        float alertClock;
        Transform threatProxy;
        readonly List<ProtectorGuard> alive = new();

        void Start()
        {
            if (vip == null) vip = GetComponent<VipTarget>();
            var director = MissionDirector.Instance;
            if (director != null) director.FirstSniperImpact += OnFirstImpact;
        }

        void OnDestroy()
        {
            var director = MissionDirector.Instance;
            if (director != null) director.FirstSniperImpact -= OnFirstImpact;
        }

        void OnFirstImpact(Vector3 shooterPosition)
        {
            if (alerted) return;

            // Still in the cars: nobody can react yet, and the actors are switched off. Keep the
            // shot and raise the alarm the moment they are all out.
            if (holdForArrival)
            {
                pendingAlert = true;
                pendingThreat = shooterPosition;
                return;
            }

            Alert(shooterPosition);
        }

        /// <summary>Everyone is out of the motorcade. Called by <see cref="MotorcadeArrival"/>.</summary>
        public void EndArrivalHold()
        {
            if (!holdForArrival) return;
            holdForArrival = false;
            if (pendingAlert)
            {
                pendingAlert = false;
                Alert(pendingThreat);
            }
        }

        void Alert(Vector3 shooterPosition)
        {
            alerted = true;
            alertClock = 0f;
            threatPosition = shooterPosition;

            var proxy = new GameObject("ThreatProxy");
            proxy.transform.position = shooterPosition;
            threatProxy = proxy.transform;

            foreach (var g in guards)
                if (g != null && !g.Health.IsDead) g.RaiseAlert(threatProxy);

            if (vip != null) vip.BeginEvacuation(shooterPosition);
        }

        void Update()
        {
            if (vip == null || holdForArrival) return;

            if (!alerted)
            {
                if (escortWhileCalm) TickEscort();
                return;
            }

            alertClock += Time.deltaTime;
            if (!CollectAlive()) return;

            Vector3 vipPos = vip.transform.position;
            Vector3 toThreat = threatPosition - vipPos;
            toThreat.y = 0f;
            if (toThreat.sqrMagnitude < 0.01f) toThreat = Vector3.forward;
            toThreat.Normalize();

            float tighten = Mathf.Clamp01(alertClock / Mathf.Max(0.01f, shieldTightenTime));
            float radius = Mathf.Lerp(calmRadius, shieldRadius, tighten);

            // Spread the survivors evenly across the arc facing the shooter.
            int n = alive.Count;
            for (int i = 0; i < n; i++)
            {
                float t = n == 1 ? 0.5f : i / (float)(n - 1);
                float angle = Mathf.Lerp(-arcSpread * 0.5f, arcSpread * 0.5f, t);
                Vector3 dir = Quaternion.AngleAxis(angle, Vector3.up) * toThreat;
                Vector3 want = vipPos + dir * radius;

                if (NavMesh.SamplePosition(want, out NavMeshHit hit, 2.5f, NavMesh.AllAreas))
                    want = hit.position;
                alive[i].AssignSlot(want);
            }
        }

        /// <summary>
        /// The moving escort. Everyone rides a ring around the target, spaced evenly, facing
        /// out — so the formation reads as people watching the street rather than people
        /// following someone.
        ///
        /// The ring is oriented to the target's heading rather than to world axes, which is what
        /// makes it look deliberate: the same guard stays on point, the same two stay on the
        /// flanks, and the whole shape swings around with him at a corner instead of scrambling.
        /// </summary>
        void TickEscort()
        {
            if (!CollectAlive()) return;

            Vector3 vipPos = vip.transform.position;
            Vector3 heading = vip.Heading;

            Vector3 right = Vector3.Cross(Vector3.up, heading);

            int n = alive.Count;
            for (int i = 0; i < n; i++)
            {
                // Evenly around the whole formation, with the first guard on point. A partial
                // arc would leave a side open, and the whole reason this exists is that the
                // target was walking around unaccompanied.
                //
                // Squashed across the direction of travel rather than a true circle: a detail
                // moving down a corridor strings out fore and aft, and on the street contract a
                // circle this wide would put the flankers off the pavement entirely.
                float a = Mathf.Deg2Rad * (360f * i / n + escortFrontBias);
                Vector3 offset = heading * (Mathf.Cos(a) * escortRadius)
                               + right * (Mathf.Sin(a) * escortRadius * escortLateralScale);

                Vector3 want = vipPos + offset;
                if (NavMesh.SamplePosition(want, out NavMeshHit hit, 3f, NavMesh.AllAreas))
                    want = hit.position;

                // Look the way they are standing relative to him: the point man watches ahead,
                // the rear guard watches behind, the flankers watch the flanks.
                alive[i].EscortTo(want, offset);
            }
        }

        /// <summary>Refills <see cref="alive"/>; false when the whole detail is down.</summary>
        bool CollectAlive()
        {
            alive.Clear();
            foreach (var g in guards)
                if (g != null && !g.Health.IsDead) alive.Add(g);
            return alive.Count > 0;
        }

        /// <summary>Editor/bootstrap helper: pull every guard parented under this detail.</summary>
        public void CollectGuards()
        {
            guards.Clear();
            guards.AddRange(GetComponentsInChildren<ProtectorGuard>(true));
        }
    }
}
