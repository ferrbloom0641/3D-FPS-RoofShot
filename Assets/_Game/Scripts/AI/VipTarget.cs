using System;
using UnityEngine;
using UnityEngine.AI;

namespace RooftopContract
{
    /// <summary>
    /// The contract target. Idles on the terrace until the first round goes off,
    /// then breaks for an exit with a lateral weave that makes leading the shot ugly.
    /// </summary>
    [RequireComponent(typeof(Damageable))]
    public class VipTarget : MonoBehaviour
    {
        [Header("Movement")]
        public float calmSpeed = 1.2f;
        public float evacSpeed = 3.4f;
        public float weaveAmplitude = 2.5f;
        public float weaveFrequency = 0.8f;
        [Tooltip("첫 발 후 겁먹는 애니메이션을 보여 준 뒤 도주하기까지의 시간.")]
        public float terrifiedDuration = 1.2f;

        [Header("Patrol")]
        public Transform[] calmWaypoints;
        public Transform[] evacPoints;
        public float waypointPause = 3f;

        [Header("One-way route")]
        [Tooltip("켜면 웨이포인트를 순환하지 않고 끝까지 한 번만 걷는다. 도보 이동/헬기 탑승 미션용.")]
        public bool oneWayRoute;
        [Tooltip("마지막 웨이포인트에 도착하면 표적이 현장을 뜬 것으로 처리한다.")]
        public bool escapesAtRouteEnd;
        [Tooltip("이 웨이포인트에 닿기 전까지는 '접근 중'이다. 그 전에는 화면 구석에서 걸어오는 중이라 " +
                 "플레이어가 할 일이 없고, HUD 는 남은 창이 아니라 도착까지를 세야 한다.")]
        public int inPlayWaypoint = 1;

        /// <summary>
        /// Still walking into the engagement — not yet somewhere the player can do anything about
        /// him. The HUD counts down to arrival during this, and to the closing window after it.
        /// </summary>
        public bool IsApproaching =>
            oneWayRoute && !routeFinished && !Evacuating && !health.IsDead && calmIndex < inPlayWaypoint;

        /// <summary>Seconds until he is in play, for the approach countdown.</summary>
        public float SecondsUntilInPlay => arrivalDelay + SecondsToWaypoint(inPlayWaypoint);

        /// <summary>
        /// Seconds before he is even on foot — set every frame by <see cref="MotorcadeArrival"/>
        /// while he is still being driven in, 0 otherwise.
        /// </summary>
        [NonSerialized] public float arrivalDelay;

        /// <summary>
        /// Which way he is travelling, flattened, or which way he is facing when stopped.
        /// The escort ring is oriented to this so the point man stays the point man.
        /// </summary>
        public Vector3 Heading
        {
            get
            {
                Vector3 v = agent != null ? agent.velocity : Vector3.zero;
                v.y = 0f;
                if (v.sqrMagnitude > 0.04f) return v.normalized;
                Vector3 f = transform.forward;
                f.y = 0f;
                return f.sqrMagnitude > 0.01f ? f.normalized : Vector3.forward;
            }
        }

        /// <summary>Fired when the target reaches a waypoint, carrying that waypoint's index.</summary>
        public event Action<int> WaypointReached;

        // There is deliberately no "seconds until he escapes" here. The walk to the door is
        // already on screen: the player can watch him close on it, and a number counting the
        // same thing down is the HUD repeating what the scope is showing. The one thing the
        // scope cannot show is the stretch before he is in play — he is a dot at the edge of
        // the frame, or not in it at all — which is the only stretch that gets a countdown.

        /// <summary>Walking time from where he is now to waypoint <paramref name="index"/>.</summary>
        float SecondsToWaypoint(int index)
        {
            if (calmWaypoints == null || calmWaypoints.Length == 0) return 0f;
            index = Mathf.Clamp(index, 0, calmWaypoints.Length - 1);
            if (calmIndex >= index) return 0f;

            float metres = 0f;
            Vector3 from = transform.position;
            for (int i = Mathf.Max(calmIndex, 0); i <= index; i++)
            {
                if (calmWaypoints[i] == null) continue;
                metres += Vector3.Distance(from, calmWaypoints[i].position);
                from = calmWaypoints[i].position;
            }

            float legsLeft = Mathf.Max(0, index - calmIndex);
            return metres / Mathf.Max(0.1f, calmSpeed) + legsLeft * waypointPause;
        }

        public bool Evacuating { get; private set; }
        public ActorSetup Setup { get; private set; }

        NavMeshAgent agent;
        Damageable health;
        Animator animator;
        int calmIndex;
        float pauseTimer;
        Vector3 evacGoal;
        float weavePhase;
        float terrifiedTimer;
        bool hasTerrifiedTrigger;
        bool routeFinished;

        void Awake()
        {
            agent = GetComponent<NavMeshAgent>();
            health = GetComponent<Damageable>();
            Setup = GetComponent<ActorSetup>();
            animator = GetComponentInChildren<Animator>();
            if (animator != null)
                foreach (var parameter in animator.parameters)
                    if (parameter.name == "Terrified") { hasTerrifiedTrigger = true; break; }
            weavePhase = UnityEngine.Random.value * 10f;
        }

        void Start()
        {
            if (agent != null) agent.speed = calmSpeed;
            health.Died += _ => MissionDirector.Instance?.ReportTargetEliminated();

            var director = MissionDirector.Instance;
            if (director != null) director.TargetGotAway += OnGotAway;
        }

        void OnDestroy()
        {
            var director = MissionDirector.Instance;
            if (director != null) director.TargetGotAway -= OnGotAway;
        }

        /// <summary>
        /// The contract is off. The target is either through the door or inside the helicopter,
        /// so it stops existing as something to shoot at — leaving him standing on an empty
        /// helipad while the HUD says he is gone is worse than removing him.
        ///
        /// This runs whether the escape came from the walk finishing or from the mission's
        /// strike window expiring, which is why the removal lives on the event rather than at
        /// the end of the route.
        /// </summary>
        void OnGotAway()
        {
            if (health.IsDead) return;
            routeFinished = true;
            gameObject.SetActive(false);
        }

        void Update()
        {
            if (health.IsDead || agent == null || !agent.isOnNavMesh) return;

            if (Evacuating && terrifiedTimer > 0f) TickTerrified();
            else if (Evacuating) TickEvac();
            else TickCalm();

            if (animator != null) animator.SetFloat("Speed", agent.velocity.magnitude);
        }

        void TickCalm()
        {
            if (calmWaypoints == null || calmWaypoints.Length == 0) return;
            if (agent.pathPending) return;
            if (routeFinished) return;

            if (agent.remainingDistance <= agent.stoppingDistance + 0.2f)
            {
                pauseTimer -= Time.deltaTime;
                if (pauseTimer > 0f) return;

                if (oneWayRoute && calmIndex >= calmWaypoints.Length - 1)
                {
                    // End of the walk: through the doorway, or up the steps into the bird.
                    routeFinished = true;
                    WaypointReached?.Invoke(calmIndex);
                    if (escapesAtRouteEnd) MissionDirector.Instance?.ReportTargetEscaped();
                    return;
                }

                // Announced on departure from a waypoint rather than on arrival at the next one,
                // which conveniently makes the very first frame fire index 0 — the walk gets its
                // opening line before he has taken a step.
                WaypointReached?.Invoke(calmIndex);

                calmIndex = oneWayRoute
                    ? calmIndex + 1
                    : (calmIndex + 1) % calmWaypoints.Length;
                agent.SetDestination(calmWaypoints[calmIndex].position);
                pauseTimer = waypointPause;
            }
        }

        void TickEvac()
        {
            // Weave perpendicular to the run so the shooter cannot simply lead a straight line.
            Vector3 forward = agent.velocity.sqrMagnitude > 0.05f ? agent.velocity.normalized : transform.forward;
            Vector3 side = Vector3.Cross(Vector3.up, forward);
            float offset = Mathf.Sin((Time.time + weavePhase) * weaveFrequency * Mathf.PI * 2f) * weaveAmplitude;
            Vector3 goal = evacGoal + side * offset;

            if (NavMesh.SamplePosition(goal, out NavMeshHit hit, 4f, NavMesh.AllAreas))
                agent.SetDestination(hit.position);
        }

        void TickTerrified()
        {
            terrifiedTimer -= Time.deltaTime;
            if (terrifiedTimer > 0f) return;
            agent.isStopped = false;
            agent.speed = evacSpeed;
        }

        public void BeginEvacuation(Vector3 threatPosition)
        {
            if (Evacuating || health.IsDead) return;
            Evacuating = true;
            evacGoal = PickEvacPoint(threatPosition);
            terrifiedTimer = terrifiedDuration;
            if (agent != null && agent.isOnNavMesh)
            {
                agent.isStopped = terrifiedTimer > 0f;
                agent.ResetPath();
                agent.speed = evacSpeed;
            }
            if (animator != null && hasTerrifiedTrigger) animator.SetTrigger("Terrified");
        }

        Vector3 PickEvacPoint(Vector3 threat)
        {
            if (evacPoints == null || evacPoints.Length == 0)
                return transform.position + (transform.position - threat).normalized * 20f;

            // Prefer the exit that puts the most distance between the VIP and the shooter.
            Transform best = evacPoints[0];
            float bestScore = float.NegativeInfinity;
            foreach (var p in evacPoints)
            {
                if (p == null) continue;
                float score = Vector3.Distance(p.position, threat) - Vector3.Distance(p.position, transform.position) * 0.5f;
                if (score > bestScore) { bestScore = score; best = p; }
            }
            return best.position;
        }
    }
}
