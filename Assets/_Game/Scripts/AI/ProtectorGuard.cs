using UnityEngine;
using UnityEngine.AI;

namespace RooftopContract
{
    /// <summary>
    /// A close-protection guard on the target terrace. Before the first shot it loiters.
    /// After it, it takes an assigned slot on the arc between the VIP and the shooter
    /// and stays glued there — that body block is the whole second-shot problem.
    /// </summary>
    [RequireComponent(typeof(Damageable))]
    public class ProtectorGuard : MonoBehaviour
    {
        [Header("Movement")]
        public float calmSpeed = 1.1f;
        [Tooltip("걷는 VIP를 호위할 때의 속도. VIP 보행(1.2)보다 빨라야 자리를 유지한다 — " +
                 "같으면 한 번 뒤처진 뒤 영원히 못 따라잡는다.")]
        public float escortSpeed = 2.2f;
        public float shieldSpeed = 4.2f;
        [Tooltip("호위 자리가 이만큼 움직여야 경로를 다시 계산한다. 매 프레임 다시 잡으면 에이전트가 떤다.")]
        public float escortRepathDistance = 0.45f;
        [Tooltip("이 경호원이 Pistol_I를 사용하는가. 아니면 SMG_P를 사용한다.")]
        public bool usesPistol;

        public bool Alerted { get; private set; }
        public ActorSetup Setup { get; private set; }
        public Damageable Health { get; private set; }

        NavMeshAgent agent;
        Animator animator;
        AlertMark mark;
        Vector3 slot;
        bool hasSlot;
        Vector3 calmAnchor;
        float calmTimer;
        Transform faceTarget;

        Vector3 escortSlot;
        Vector3 escortLook;
        Vector3 escortLastPath;
        bool hasEscortSlot;

        void Awake()
        {
            agent = GetComponent<NavMeshAgent>();
            Health = GetComponent<Damageable>();
            Setup = GetComponent<ActorSetup>();
            animator = GetComponentInChildren<Animator>();
            if (animator != null)
            {
                foreach (var parameter in animator.parameters)
                    if (parameter.name == "IsPistol")
                    {
                        animator.SetBool("IsPistol", usesPistol);
                        break;
                    }
            }
            calmAnchor = transform.position;
        }

        void Start()
        {
            // A guard who rode in with the motorcade is switched on mid-mission, and the detail
            // can hand him an escort station or an alert in that same frame — before this runs.
            // Resetting to the loitering pace then would leave him walking slower than the man
            // he is meant to keep up with.
            if (agent != null && !hasEscortSlot && !Alerted) agent.speed = calmSpeed;
        }

        void Update()
        {
            if (Health.IsDead) return;

            if (Alerted && hasSlot)
            {
                if (agent != null && agent.isOnNavMesh) agent.SetDestination(slot);
                if (faceTarget != null)
                {
                    Vector3 look = faceTarget.position - transform.position;
                    look.y = 0f;
                    if (look.sqrMagnitude > 0.01f)
                        transform.rotation = Quaternion.Slerp(transform.rotation,
                            Quaternion.LookRotation(look), 8f * Time.deltaTime);
                }
            }
            else if (!Alerted && hasEscortSlot)
            {
                TickEscort();
            }
            else if (!Alerted)
            {
                TickCalm();
            }

            if (animator != null && agent != null) animator.SetFloat("Speed", agent.velocity.magnitude);
        }

        void TickCalm()
        {
            if (agent == null || !agent.isOnNavMesh) return;
            calmTimer -= Time.deltaTime;
            if (calmTimer <= 0f)
            {
                Vector2 r = Random.insideUnitCircle * 2.2f;
                Vector3 wander = calmAnchor + new Vector3(r.x, 0f, r.y);
                if (NavMesh.SamplePosition(wander, out NavMeshHit hit, 3f, NavMesh.AllAreas))
                    agent.SetDestination(hit.position);
                calmTimer = Random.Range(2.5f, 5f);
            }
        }

        /// <summary>
        /// Walk with the principal, then stand and watch outward once on station.
        ///
        /// Rotation is only taken over after arriving. While actually walking the NavMeshAgent
        /// turns the body toward its own velocity, which is what a person moving looks like;
        /// forcing the outward facing the whole time would have them crabbing sideways down the
        /// pavement.
        /// </summary>
        void TickEscort()
        {
            if (agent == null || !agent.isOnNavMesh) return;

            // Repath only when the station has actually moved. Re-issuing SetDestination every
            // frame against a walking target makes the agent stutter in place.
            if ((escortSlot - escortLastPath).sqrMagnitude > escortRepathDistance * escortRepathDistance)
            {
                agent.SetDestination(escortSlot);
                escortLastPath = escortSlot;
            }

            bool onStation = !agent.pathPending &&
                             agent.remainingDistance <= agent.stoppingDistance + 0.35f;
            if (!onStation || escortLook.sqrMagnitude < 0.01f) return;

            transform.rotation = Quaternion.Slerp(transform.rotation,
                Quaternion.LookRotation(escortLook), 5f * Time.deltaTime);
        }

        /// <summary>
        /// Take a station on the moving escort ring. <paramref name="lookDirection"/> is the way
        /// out of the formation from the principal, so a guard watches the side they are on.
        /// </summary>
        public void EscortTo(Vector3 worldPosition, Vector3 lookDirection)
        {
            if (Alerted || Health.IsDead) return;

            if (!hasEscortSlot)
            {
                hasEscortSlot = true;
                escortLastPath = Vector3.positiveInfinity;   // force the first path
                if (agent != null) agent.speed = escortSpeed;
            }

            escortSlot = worldPosition;
            lookDirection.y = 0f;
            if (lookDirection.sqrMagnitude > 0.01f) escortLook = lookDirection.normalized;
        }

        /// <summary>First shot heard: pop the "!" and switch to shield speed.</summary>
        public void RaiseAlert(Transform lookAt)
        {
            if (Alerted || Health.IsDead) return;
            Alerted = true;
            faceTarget = lookAt;
            if (agent != null)
            {
                agent.speed = shieldSpeed;
                agent.angularSpeed = 720f;
                agent.acceleration = 20f;
            }

            // Anchored to the actor root at the measured height rather than to a head bone:
            // it lands in the same place whether the character is rigged or a bare voxel mesh.
            float h = Setup != null ? Setup.ActorHeight : 1.8f;
            mark = AlertMark.Attach(transform, h + 0.45f);
        }

        public void AssignSlot(Vector3 worldPosition)
        {
            slot = worldPosition;
            hasSlot = true;
        }
    }
}
