using System.Collections.Generic;
using UnityEngine;

namespace RooftopContract
{
    /// <summary>
    /// The street contract's opening: three cars drive in along the cross street, pull up at the
    /// far kerb, and the detail gets out — guards from the lead car, the target from the middle
    /// one, guards from the rear one — before setting off on foot.
    ///
    /// Everyone riding is a real actor placed by the generator where they will step out. They
    /// are switched off for the drive and switched on in exit order once the column has
    /// stopped, which is what "getting out" looks like from 300m. Until then the detail is held
    /// (<see cref="ProtectionDetail.holdForArrival"/>): a round that lands while they are still
    /// inside is remembered and the alarm goes off the moment the last door opens, so firing
    /// early costs the surprise without breaking the arrival.
    ///
    /// The cars are built at their stops, so the scene reads correctly in the editor. Start
    /// rolls them back out of view and the drive brings them home.
    /// </summary>
    public class MotorcadeArrival : MonoBehaviour
    {
        [Header("Refs — 씬 생성기가 채운다")]
        [Tooltip("앞차 / 표적 차 / 뒤차. 정차 위치에 배치되어 있다.")]
        public Transform[] cars;
        [Tooltip("하차 순서대로. 정차 지점 옆 인도에 배치되어 있고, 주행 중에는 꺼져 있다.")]
        public GameObject[] passengers;
        public ProtectionDetail detail;
        public VipTarget vip;

        [Header("Drive")]
        [Tooltip("진행 방향 (월드).")]
        public Vector3 heading = Vector3.left;
        [Tooltip("출발 지점에서 정차 지점까지 거리 (m).")]
        public float approachDistance = 62f;
        [Tooltip("미션 시작 후 출발까지 (초). 저격 위치 인트로(3.2초)가 끝난 뒤에 보이도록.")]
        public float startDelay = 3.5f;
        public float cruiseSpeed = 11f;
        [Tooltip("이 거리부터 일정하게 감속해 정차한다 (m).")]
        public float brakeDistance = 15f;

        [Header("Unload")]
        [Tooltip("정차 후 첫 문이 열리기까지 (초).")]
        public float unloadDelay = 0.8f;
        [Tooltip("탑승자 사이 하차 간격 (초).")]
        public float unloadInterval = 0.5f;

        [Header("Radio")]
        [TextArea(1, 2)] public string approachCue = "";
        public float cueDuration = 4.5f;

        enum State { Waiting, Driving, Unloading, Done }

        State state;
        float clock;
        float remaining;      // metres left to the stop
        float speed;
        float unloadClock;
        int released;
        Vector3[] stops;
        readonly List<Transform>[] wheels = new List<Transform>[3];

        const float WheelRadius = 0.39f;   // the Awbme cars' tyre centres sit 0.39m up

        void Start()
        {
            heading.y = 0f;
            heading = heading.sqrMagnitude > 0.01f ? heading.normalized : Vector3.left;

            stops = new Vector3[cars.Length];
            for (int i = 0; i < cars.Length; i++)
            {
                if (cars[i] == null) continue;
                stops[i] = cars[i].position;
                wheels[Mathf.Min(i, wheels.Length - 1)] = FindWheels(cars[i]);
            }

            // Every Awake in the scene has run by now, which is the point of doing this in Start
            // rather than Awake: a passenger switched off before its own Awake would come back
            // without its Damageable wired, and the HUD and the detail read it from frame one.
            foreach (var p in passengers)
                if (p != null) p.SetActive(false);

            remaining = approachDistance;
            PlaceCars();
            state = State.Waiting;
        }

        void Update()
        {
            switch (state)
            {
                case State.Waiting:
                    clock += Time.deltaTime;
                    if (clock >= startDelay)
                    {
                        state = State.Driving;
                        speed = cruiseSpeed;
                        if (!string.IsNullOrEmpty(approachCue) && SubtitleFeed.Instance != null)
                            SubtitleFeed.Instance.Show(approachCue, cueDuration);
                    }
                    break;

                case State.Driving:
                    TickDrive();
                    break;

                case State.Unloading:
                    TickUnload();
                    break;
            }

            if (vip != null) vip.arrivalDelay = SecondsUntilTargetOut();
        }

        void TickDrive()
        {
            // Cruise, then a constant deceleration that runs out exactly at the stop.
            float decel = cruiseSpeed * cruiseSpeed / (2f * Mathf.Max(0.1f, brakeDistance));
            speed = remaining > brakeDistance
                ? cruiseSpeed
                : Mathf.Max(0.4f, Mathf.Sqrt(2f * decel * remaining));

            float step = speed * Time.deltaTime;
            remaining = Mathf.Max(0f, remaining - step);
            PlaceCars();
            SpinWheels(step);

            if (remaining <= 0f)
            {
                speed = 0f;
                state = State.Unloading;
                unloadClock = 0f;
            }
        }

        void TickUnload()
        {
            unloadClock += Time.deltaTime;
            while (released < passengers.Length &&
                   unloadClock >= unloadDelay + released * unloadInterval)
            {
                var p = passengers[released];
                if (p != null) p.SetActive(true);
                released++;
            }

            if (released >= passengers.Length)
            {
                state = State.Done;
                if (detail != null) detail.EndArrivalHold();
            }
        }

        void PlaceCars()
        {
            Vector3 back = -heading * remaining;
            for (int i = 0; i < cars.Length; i++)
                if (cars[i] != null) cars[i].position = stops[i] + back;
        }

        void SpinWheels(float metres)
        {
            float degrees = metres / WheelRadius * Mathf.Rad2Deg;
            foreach (var list in wheels)
            {
                if (list == null) continue;
                foreach (var w in list) w.Rotate(Vector3.right, degrees, Space.Self);
            }
        }

        static List<Transform> FindWheels(Transform car)
        {
            var list = new List<Transform>();
            foreach (var t in car.GetComponentsInChildren<Transform>(true))
                if (t != car && t.name.Contains("Tire")) list.Add(t);
            return list;
        }

        /// <summary>
        /// Seconds until the target is out of the car. Feeds the HUD's approach countdown, which
        /// would otherwise only know about the walk and read "0" for the whole drive.
        /// </summary>
        float SecondsUntilTargetOut()
        {
            int vipIndex = System.Array.IndexOf(passengers, vip != null ? vip.gameObject : null);
            if (vipIndex < 0 || released > vipIndex) return 0f;

            float exit = unloadDelay + vipIndex * unloadInterval;
            switch (state)
            {
                case State.Waiting:
                    return (startDelay - clock) + DriveTime(remaining) + exit;
                case State.Driving:
                    return DriveTime(remaining) + exit;
                case State.Unloading:
                    return Mathf.Max(0f, exit - unloadClock);
                default:
                    return 0f;
            }
        }

        float DriveTime(float metres)
        {
            float b = Mathf.Max(0.1f, brakeDistance);
            float cruise = Mathf.Max(0.1f, cruiseSpeed);
            // Constant deceleration from cruise to rest over b metres takes 2b / cruise.
            if (metres > b) return (metres - b) / cruise + 2f * b / cruise;
            return 2f * b / cruise * Mathf.Sqrt(metres / b);
        }
    }
}
