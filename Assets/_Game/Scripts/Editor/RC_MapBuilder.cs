using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace RooftopContract.EditorTools
{
    /// <summary>
    /// Builds the city and the two gameplay rooftops.
    ///
    /// The skyline comes straight from the Polyeler building pack, scaled to measured heights.
    /// The two rooftops the game actually plays on are constructed from primitives instead:
    /// the pack's roofs are decorative shapes with no guarantee of a flat, NavMesh-able surface,
    /// and both phases depend on reliable footing and reliable cover.
    /// </summary>
    public static class RC_MapBuilder
    {
        // ---- layout (per-mission; defaults are mission 1) ----
        //
        // The player's nest is identical in every contract — the entire defense phase is level
        // design baked into that one roof, and rebuilding it per mission would mean retuning it
        // per mission. What changes between contracts is what sits at the other end of the
        // scope, so the venue and its ground height are per-mission and the nest is derived
        // from them.
        public static TargetVenue Venue = TargetVenue.RooftopTerrace;

        /// <summary>Height of the ground the target stands on: a terrace at 34m, a street at 0.</summary>
        public static float TargetGroundY = 34f;

        // Elevation over the terrace. Keep this generous: the terrace parapet shadows the roof
        // floor at shallow angles — at +18m over 250m the depression is 4.1deg and a 1.15m wall
        // hides 16m of a 24m-deep roof. Lowering it is the strongest difficulty lever there is,
        // which is exactly why mission 2 uses it.
        public static float ElevationAdvantage = 30f;
        public static float EngagementRange = 250f;

        public static float NestHeight => TargetGroundY + ElevationAdvantage;
        /// <summary>Origin of the target's venue, whatever kind of venue it is.</summary>
        public static Vector3 TerraceCenter => new Vector3(0f, TargetGroundY, 0f);
        public static Vector3 NestCenter => new Vector3(0f, NestHeight, -EngagementRange);

        /// <summary>
        /// Depression angle from the nest's eye to the target's feet, in degrees. The sniper
        /// spawns pitched to this so the venue is under the crosshair on the first frame.
        /// It used to be the constant 6.7 — correct for mission 1's 30m over 250m and wrong for
        /// every other contract, which is only survivable while there is exactly one contract.
        /// </summary>
        public static float SpawnPitch =>
            Mathf.Atan2(NestHeight + 1.15f - TargetGroundY, EngagementRange) * Mathf.Rad2Deg;

        const float TerraceSizeX = 30f, TerraceSizeZ = 24f;

        // The nest deck runs deep, not wide. Its front edge stays where the sniping happens
        // (z = +10 from NestCenter, which is what the camera pitch and the parapet heights were
        // tuned against); everything added for the defense phase is added *behind* the player,
        // giving the assault 25 m to cross in three bounds instead of one sprint.
        const float NestSizeX = 28f, NestSizeZ = 34f;
        const float NestDeckShift = -7f;    // deck centre, relative to NestCenter
        static Vector3 Deck => NestCenter + new Vector3(0f, 0f, NestDeckShift);

        const float ParapetHeight = 1.15f, ParapetThickness = 0.6f;

        // How far past the edge of their own cover an attacker has to step before a bullet aimed
        // at their torso stops hitting the cover instead. Half a shoulder plus a little slack.
        const float ShoulderClearance = 0.45f;

        // How far below a play deck the skyline building under it is scaled to stop. The deck
        // slabs are 0.5m thick, so this leaves 0.1m of air between the pack building's roof and
        // the underside of the deck — enough that no two surfaces share a depth value.
        const float DeckClearance = 0.6f;

        public class MapRefs
        {
            public Transform root;
            public Transform terrace, nest;
            public Transform terraceSurface, nestSurface;
            public List<Transform> vipWaypoints = new List<Transform>();
            public List<Transform> vipEvacPoints = new List<Transform>();
            public List<Transform> guardPosts = new List<Transform>();
            public List<Transform> assaultSpawns = new List<Transform>();
            public List<CoverPoint> assaultCover = new List<CoverPoint>();
            public Light assaultDoorLight;
            public Transform sniperNest, coverAnchor;

            /// <summary>Where the VIP is placed. Venues put their target where the venue wants it.</summary>
            public Vector3 targetSpawn;
            /// <summary>Dressing positions for civilians, so each venue populates itself sensibly.</summary>
            public List<Transform> civilianPosts = new List<Transform>();
            /// <summary>
            /// One line of radio traffic per VIP waypoint, index-aligned. Authored by the venue
            /// because the venue is what knows there is a bus shelter at waypoint three.
            /// Left empty by venues whose target does not walk.
            /// </summary>
            public List<string> routeCues = new List<string>();
            /// <summary>The target walks its waypoints once and leaves rather than looping a patrol.</summary>
            public bool targetOneWayRoute;
            /// <summary>Reaching the last waypoint means the target got out alive.</summary>
            public bool targetEscapesAtEnd;

            /// <summary>
            /// The motorcade the target arrives in, built where it comes to rest: lead, principal,
            /// rear. Null for venues where the target is already on site.
            /// </summary>
            public Transform[] motorcadeCars;
            /// <summary>Which motorcade car the occupant of each guard post rides in, index-aligned with guardPosts.</summary>
            public List<int> guardPostCar = new List<int>();
            /// <summary>Which way the motorcade drives, and how far it travels to reach its stops.</summary>
            public Vector3 motorcadeHeading;
            public float motorcadeApproach;
            /// <summary>Radio line for the moment the motorcade sets off.</summary>
            public string motorcadeCue;
        }

        [MenuItem("Rooftop Contract/도구/씬 — 맵만 생성", priority = 1)]
        public static void BuildMapMenu()
        {
            Build(12345);
            Debug.Log("[Rooftop Contract] Map built. Run 'Build Playable Scene' for the full wiring.");
        }

        public static MapRefs Build(MissionDefinition mission)
        {
            EngagementRange = mission != null ? mission.engagementRange : 250f;
            ElevationAdvantage = mission != null ? mission.elevationAdvantage : 30f;
            Venue = mission != null ? mission.venue : TargetVenue.RooftopTerrace;
            TargetGroundY = mission != null ? mission.targetGroundY : 34f;
            return Build(mission != null ? mission.mapSeed : 20260901);
        }

        public static MapRefs Build(int seed)
        {
            var old = GameObject.Find("MAP");
            if (old != null) Object.DestroyImmediate(old);

            Random.InitState(seed);
            RC_BuildUtil.ClearBoundsCache();

            var refs = new MapRefs();
            var root = new GameObject("MAP").transform;
            refs.root = root;

            BuildGround(root);
            BuildSkyline(root);
            BuildStreetProps(root);

            refs.terrace = BuildTargetVenue(root, refs).transform;
            refs.nest = BuildNest(root, refs).transform;

            return refs;
        }

        /// <summary>
        /// The far end of the shot. One of these per contract; the nest never changes.
        ///
        /// Whatever a venue builds, it owes the rest of the generator the same four things:
        /// a NavMesh-able surface, a target spawn, a route for the target, and guard posts.
        /// </summary>
        static GameObject BuildTargetVenue(Transform root, MapRefs refs)
        {
            switch (Venue)
            {
                case TargetVenue.StreetWalk: return BuildStreetVenue(root, refs);
                case TargetVenue.Helipad: return BuildHelipadVenue(root, refs);
                default: return BuildTerrace(root, refs);
            }
        }

        // ------------------------------------------------------------------ ground

        static void BuildGround(Transform root)
        {
            var group = RC_BuildUtil.Empty(root, "Ground");

            // One big slab rather than a Unity plane: primitives keep the poly budget honest
            // and the ground is never walked on, only looked at.
            RC_BuildUtil.Box(group.transform, "GroundSlab",
                new Vector3(0f, -0.5f, -EngagementRange * 0.5f),
                new Vector3(600f, 1f, 700f), RC_Palette.Asphalt);

            // A simple road cross so the 250m gap reads as real distance rather than empty void.
            var roadMat = RC_Palette.ConcreteDark;
            for (int i = 0; i < 5; i++)
            {
                float z = -EngagementRange * 0.5f + (i - 2) * 70f;
                RC_BuildUtil.Box(group.transform, $"Road_H{i}", new Vector3(0f, 0.02f, z),
                    new Vector3(520f, 0.06f, 14f), roadMat, 0f, false);
            }
            for (int i = 0; i < 5; i++)
            {
                float x = (i - 2) * 78f;
                RC_BuildUtil.Box(group.transform, $"Road_V{i}", new Vector3(x, 0.02f, -EngagementRange * 0.5f),
                    new Vector3(14f, 0.06f, 620f), roadMat, 0f, false);
            }
        }

        // ------------------------------------------------------------------ skyline

        static void BuildSkyline(Transform root)
        {
            var group = RC_BuildUtil.Empty(root, "Skyline");
            var buildings = RC_BuildUtil.LoadPrefabsInFolder("Assets/Polyeler/SimpleBuildingPack/Prefabs/Building");
            if (buildings.Count == 0)
            {
                Debug.LogWarning("[Rooftop Contract] No building prefabs found — skyline skipped.");
                return;
            }

            // The tower the player stands on, and — only when the venue is up in the air — the
            // one the target stands on. A street contract has no target tower: the venue *is*
            // the ground, and a building scaled to zero height is a flat sliver on the pavement.
            //
            // Both are scaled to sit BELOW their play deck, not level with it. Scaled to the deck
            // height exactly, the pack building's roof plane and the deck slab's top face land on
            // the same y to the float — two upward-facing surfaces at identical depth — and the
            // floor the player is standing on flickers wherever the two overlap. The decks are
            // 0.5m slabs, so dropping the tower by 0.6 leaves the roof 0.1m clear underneath and
            // the deck still reads as the top of the building.
            if (TargetGroundY > 1f)
            {
                var targetTower = RC_BuildUtil.PlaceScaledToHeight(
                    buildings[Random.Range(0, buildings.Count)], group.transform,
                    new Vector3(TerraceCenter.x, 0f, TerraceCenter.z), TargetGroundY - DeckClearance, 0f);
                targetTower.name = "Tower_Target";
                if (Venue == TargetVenue.RooftopTerrace)
                    SightlineColliderFitter.FitToVisibleMeshes(targetTower, true);
            }
            RC_BuildUtil.PlaceScaledToHeight(buildings[Random.Range(0, buildings.Count)], group.transform,
                new Vector3(NestCenter.x, 0f, NestCenter.z), NestHeight - DeckClearance, 180f)
                .name = "Tower_Nest";

            // Corridor framing: two tall slabs either side of the sightline so the shot has to be
            // threaded between them. This is what turns "a rooftop" into "the only rooftop".
            //
            // The street contract replaces all three with the boulevard walls (BuildBoulevard):
            // three loose towers at odd angles were exactly the "everything looks like a clue"
            // view, and two of them would stand inside the boulevard's building line.
            if (Venue != TargetVenue.StreetWalk)
            {
                var corridorL = RC_BuildUtil.PlaceScaledToHeight(buildings[2 % buildings.Count], group.transform,
                    new Vector3(-34f, 0f, -150f), 66f, 15f);
                corridorL.name = "Tower_CorridorL";
                var corridorR = RC_BuildUtil.PlaceScaledToHeight(buildings[5 % buildings.Count], group.transform,
                    new Vector3(38f, 0f, -108f), 58f, -20f);
                corridorR.name = "Tower_CorridorR";
                // One mid-height block that clips the right-hand third of the terrace approach,
                // so lateral position on the nest actually changes what the player can see.
                var occluder = RC_BuildUtil.PlaceScaledToHeight(buildings[8 % buildings.Count], group.transform,
                    new Vector3(26f, 0f, -46f), 40f, 8f);
                occluder.name = "Tower_Occluder";

                if (Venue == TargetVenue.RooftopTerrace)
                {
                    SightlineColliderFitter.FitToVisibleMeshes(corridorL, true);
                    SightlineColliderFitter.FitToVisibleMeshes(corridorR, true);
                    SightlineColliderFitter.FitToVisibleMeshes(occluder, true);
                }
            }

            // Filler skyline on a jittered grid, skipping the sightline corridor and the two towers.
            for (int gx = -3; gx <= 3; gx++)
            {
                for (int gz = -1; gz <= 5; gz++)
                {
                    Vector3 cell = new Vector3(gx * 78f, 0f, -gz * 70f + 40f);
                    if (IsReserved(cell)) continue;

                    cell += new Vector3(Random.Range(-14f, 14f), 0f, Random.Range(-12f, 12f));
                    float height = Random.Range(18f, 74f);
                    var prefab = buildings[Random.Range(0, buildings.Count)];
                    float yaw = Random.Range(0, 4) * 90f;

                    // The street contract edits two things out of its skyline. Both are decided
                    // after this cell's random numbers are drawn, so every other cell comes out
                    // exactly as it did before.
                    if (Venue == TargetVenue.StreetWalk)
                    {
                        // Bld_-1_2 stands in the view down the boulevard.
                        if (gx == -1 && gz == 2) continue;
                        prefab = NotParkingGarage(prefab, buildings);
                    }

                    var go = RC_BuildUtil.PlaceScaledToHeight(prefab, group.transform, cell, height, yaw);
                    if (go != null)
                    {
                        go.name = $"Bld_{gx}_{gz}";
                        // Mission 1's generated Bld_1_5 sits beside the visible firing lane.
                        // Its prefab box spans empty parts of the silhouette, so use the mesh
                        // itself as the collider instead of stopping rounds in open air.
                        if (Venue == TargetVenue.RooftopTerrace && gx == 1 && gz == 5)
                            SightlineColliderFitter.FitToVisibleMeshes(go, true);
                    }
                }
            }

            // After the filler, so the filler grid draws the same random numbers it always has.
            if (Venue == TargetVenue.StreetWalk) BuildBoulevard(group.transform, buildings);

            RC_BuildUtil.MarkStaticRecursive(group);
        }

        // ------------------------------------------------------------------ boulevard (street contract)

        // Asphalt from the centre line to the kerb, and the pavement between kerb and building line.
        const float BoulevardHalfWidth = 20f;
        const float BoulevardPavement = 6f;
        static float BoulevardBuildingLine => BoulevardHalfWidth + BoulevardPavement;

        // The cross street the boulevard runs into. Its carriageway is BuildGround's Road_H at
        // z −10 (−EngagementRange/2 + 140 for the street contract's 300m), 14m wide; the street
        // venue owns the pavement on the far side and BuildStreetVenue adds the near one.
        const float CrossStreetNearZ = -17f, CrossStreetFarZ = -3f;
        const float CrossStreetNearPavementZ = -21f;

        /// <summary>
        /// Contract 2's sightline runs straight down one wide boulevard with an unbroken wall of
        /// buildings on each side, ending on the cross street where the target walks.
        ///
        /// From the nest the old layout was open city in every direction — towers at odd angles,
        /// gaps, roofs — and every one of them looked as much like "the place" as the street did.
        /// A canyon has one end. The eye runs down the road to the frontage at the bottom of it,
        /// and that is where the drop-off and the walk are.
        ///
        /// The walls stop short at both ends, for two different reasons:
        ///   · near end: the extraction helicopter banks in over the left-hand side between
        ///     roughly 25 and 55m from the nest, at about 60m up. Nothing may stand there.
        ///   · far end: the cross street (Road_H, z −17..−3) and its near pavement (z −21..−17)
        ///     have to stay open, or the walls would close over the road the motorcade arrives on.
        /// From the nest, the mouth this leaves shows about ±27m of the cross street — the whole
        /// stop (x −26.5..−6) and the walk until the right-hand wall closes over it at about
        /// x +28, which is the street contract's deadline.
        ///
        /// Built under the skyline, never under the venue: the NavMesh bake treats every mesh
        /// under the venue as ground.
        /// </summary>
        static void BuildBoulevard(Transform parent, List<GameObject> buildings)
        {
            var group = RC_BuildUtil.Empty(parent, "Boulevard").transform;

            float startZ = NestCenter.z + 65f;
            const float endZ = CrossStreetNearPavementZ;
            float length = endZ - startZ;
            float midZ = (startZ + endZ) * 0.5f;

            // Same material and height as the city's other roads, so where it overlaps them the
            // two surfaces are indistinguishable rather than fighting. It runs on to the cross
            // street's near edge, so the boulevard meets it as a T-junction.
            float roadStart = startZ - 15f;
            RC_BuildUtil.Box(group, "Boulevard_Road", new Vector3(0f, 0.02f, (roadStart + CrossStreetNearZ) * 0.5f),
                new Vector3(BoulevardHalfWidth * 2f, 0.06f, CrossStreetNearZ - roadStart), RC_Palette.ConcreteDark, 0f, false);
            foreach (float side in new[] { -1f, 1f })
            {
                RC_BuildUtil.Box(group, side < 0 ? "Boulevard_PavementL" : "Boulevard_PavementR",
                    new Vector3(side * (BoulevardHalfWidth + BoulevardPavement * 0.5f), 0.09f, midZ),
                    new Vector3(BoulevardPavement, 0.18f, length), RC_Palette.Concrete, 0f, false);
            }

            if (buildings.Count == 0) return;

            // Tall enough to fill the sides of the frame from a 59m eye, short enough that the
            // nearest ones do not tower over the nest.
            const float minHeight = 42f, maxHeight = 72f;
            // How far back from the building line a block may run, and how much frontage it may
            // take. A pack prefab scaled to wall height can come out 80m wide; past these it is
            // swapped for another, and failing that shrunk.
            const float maxDepth = 30f, maxFrontage = 34f;
            const float gap = 0.6f;

            foreach (float side in new[] { -1f, 1f })
            {
                float z = startZ;
                int n = 0;
                while (z < endZ - 6f)
                {
                    float height = Random.Range(minHeight, maxHeight);
                    float yaw = Random.Range(0, 4) * 90f;
                    GameObject prefab = null;
                    float scale = 1f;
                    Vector2 footprint = Vector2.zero;   // x = depth from the line, y = frontage along z
                    for (int attempt = 0; attempt < 8; attempt++)
                    {
                        prefab = buildings[Random.Range(0, buildings.Count)];
                        // Treated as never fitting, so the draw moves on exactly as it would
                        // for an oversized block.
                        if (IsParkingGarage(prefab)) continue;
                        footprint = FootprintAt(prefab, yaw, height, out scale);
                        if (footprint.x <= maxDepth && footprint.y <= maxFrontage) break;
                    }
                    prefab = NotParkingGarage(prefab, buildings);
                    footprint = FootprintAt(prefab, yaw, height, out scale);
                    float fit = Mathf.Min(1f, maxDepth / footprint.x, maxFrontage / footprint.y);
                    if (fit < 1f)
                    {
                        height *= fit;
                        footprint = FootprintAt(prefab, yaw, height, out scale);
                    }

                    // The last block is not allowed to run out over the cross street.
                    if (z + footprint.y > endZ)
                    {
                        float shrink = (endZ - z) / footprint.y;
                        height *= shrink;
                        footprint = FootprintAt(prefab, yaw, height, out scale);
                    }

                    // Put the measured footprint's inner edge on the building line, whatever
                    // the prefab's pivot is.
                    Bounds b = RC_BuildUtil.MeasurePrefab(prefab);
                    Vector3 pivotOffset = Quaternion.Euler(0f, yaw, 0f) * new Vector3(b.center.x, 0f, b.center.z) * scale;
                    Vector3 centre = new Vector3(side * (BoulevardBuildingLine + footprint.x * 0.5f), 0f,
                        z + footprint.y * 0.5f);
                    var go = RC_BuildUtil.PlaceScaledToHeight(prefab, group, centre - pivotOffset, height, yaw);
                    if (go != null) go.name = $"Bld_Boulevard{(side < 0 ? "L" : "R")}_{n++}";

                    z += footprint.y + gap;
                }
            }
        }

        /// <summary>
        /// A prefab's ground footprint once scaled to <paramref name="height"/> and turned by a
        /// multiple of 90 degrees: x across the boulevard, y along it.
        /// </summary>
        static Vector2 FootprintAt(GameObject prefab, float yaw, float height, out float scale)
        {
            Bounds b = RC_BuildUtil.MeasurePrefab(prefab);
            scale = height / Mathf.Max(0.01f, b.size.y);
            bool turned = Mathf.RoundToInt(yaw / 90f) % 2 != 0;
            float sx = (turned ? b.size.z : b.size.x) * scale;
            float sz = (turned ? b.size.x : b.size.z) * scale;
            return new Vector2(sx, sz);
        }

        // Building7 is the pack's parking garage — the only prefab with no glass, floored in the
        // road material. Down the street contract's boulevard it reads as a landmark, the "P"
        // sign at the end of the canyon, so that contract swaps it for the next prefab along.
        const string ParkingGaragePrefab = "Building7";

        static bool IsParkingGarage(GameObject prefab) => prefab != null && prefab.name == ParkingGaragePrefab;

        static GameObject NotParkingGarage(GameObject prefab, List<GameObject> buildings)
        {
            if (!IsParkingGarage(prefab)) return prefab;
            int i = buildings.IndexOf(prefab);
            for (int step = 1; step < buildings.Count; step++)
            {
                var next = buildings[(i + step) % buildings.Count];
                if (!IsParkingGarage(next)) return next;
            }
            return prefab;
        }

        static bool IsReserved(Vector3 cell)
        {
            // Keep the two play towers and the shooting corridor clear.
            if (Vector2.Distance(new Vector2(cell.x, cell.z), new Vector2(TerraceCenter.x, TerraceCenter.z)) < 46f) return true;
            if (Vector2.Distance(new Vector2(cell.x, cell.z), new Vector2(NestCenter.x, NestCenter.z)) < 46f) return true;
            // Corridor: a 40m-wide lane down the sightline.
            if (Mathf.Abs(cell.x) < 20f && cell.z < 10f && cell.z > -EngagementRange) return true;
            return false;
        }

        /// <summary>
        /// The footprint the street venue owns. Generic city dressing keeps out of it, because
        /// inside this box every object is placed for a gameplay reason.
        /// </summary>
        static bool IsInsideStreetVenue(float x, float z)
        {
            if (Venue != TargetVenue.StreetWalk) return false;
            return x > -34f && x < 32f && z > -12f && z < 14f;
        }

        static void BuildStreetProps(Transform root)
        {
            var group = RC_BuildUtil.Empty(root, "StreetProps");
            var kitFolder = "Assets/KayKit_City_Builder_Bits_1.0_FREE/KayKit_City_Builder_Bits_1.0_FREE/Assets/fbx (unity)";

            string[] carNames = { "car_sedan", "car_taxi", "car_hatchback", "car_stationwagon", "car_police" };
            var cars = new List<GameObject>();
            foreach (var n in carNames)
            {
                var go = AssetDatabase.LoadAssetAtPath<GameObject>($"{kitFolder}/{n}.fbx");
                if (go != null) cars.Add(go);
            }
            var streetlight = AssetDatabase.LoadAssetAtPath<GameObject>($"{kitFolder}/streetlight.fbx");

            // The city-bit kit is roughly 1 unit per metre-ish tile; scale it up so a car reads
            // as a car from 52m above rather than as a speck.
            const float kitScale = 3.2f;

            var kitMat = RC_Palette.CityBits;

            if (cars.Count > 0)
            {
                for (int i = 0; i < 40; i++)
                {
                    float z = Random.Range(-EngagementRange - 40f, 60f);
                    float x = Mathf.Round(Random.Range(-2f, 2f)) * 78f + Random.Range(-5f, 5f);
                    // The street venue builds its own kerbside cars as deliberate cover. A random
                    // one parked on top of them is at best redundant and at worst a car sitting
                    // in the middle of the pavement the target has to walk down.
                    if (IsInsideStreetVenue(x, z)) continue;
                    // The street contract opens on an empty road: the only cars in the view down
                    // the boulevard are the three that bring the target in.
                    if (Venue == TargetVenue.StreetWalk && Mathf.Abs(x) < BoulevardBuildingLine) continue;
                    var prefab = cars[Random.Range(0, cars.Count)];
                    var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, group.transform);
                    if (go == null) continue;
                    go.transform.position = new Vector3(x, 0f, z);
                    go.transform.rotation = Quaternion.Euler(0f, Random.Range(0, 2) * 180f, 0f);
                    go.transform.localScale = Vector3.one * kitScale;
                    Retexture(go, kitMat);
                }
            }

            if (streetlight != null)
            {
                // On the street contract the road under the sightline is the boulevard, and
                // ±9 would stand the lamps in the middle of its lanes. Move them to its kerbs.
                float lampX = Venue == TargetVenue.StreetWalk ? BoulevardHalfWidth + 1f : 9f;
                for (int i = 0; i < 26; i++)
                {
                    float z = -EngagementRange - 30f + i * 12f;
                    foreach (float side in new[] { -lampX, lampX })
                    {
                        if (IsInsideStreetVenue(side, z)) continue;
                        var go = (GameObject)PrefabUtility.InstantiatePrefab(streetlight, group.transform);
                        if (go == null) continue;
                        go.transform.position = new Vector3(side, 0f, z);
                        go.transform.rotation = Quaternion.Euler(0f, side > 0 ? 180f : 0f, 0f);
                        go.transform.localScale = Vector3.one * kitScale;
                        Retexture(go, kitMat);
                    }
                }
            }

            RC_BuildUtil.MarkStaticRecursive(group);
        }

        // ------------------------------------------------------------------ target terrace

        static GameObject BuildTerrace(Transform root, MapRefs refs)
        {
            var group = RC_BuildUtil.Empty(root, "TargetTerrace", TerraceCenter);
            var t = group.transform;

            var floor = RC_BuildUtil.Box(t, "Surface", TerraceCenter + Vector3.down * 0.25f,
                new Vector3(TerraceSizeX, 0.5f, TerraceSizeZ), RC_Palette.RoofTar);
            refs.terraceSurface = floor.transform;

            // Full-height wall on three sides, but only a kerb on the side facing the nest.
            // A 1.15m parapet there would shadow ~10m of floor even at this angle; 0.35m
            // shadows under 3m, which keeps the whole terrace in play.
            BuildParapet(t, TerraceCenter, TerraceSizeX, TerraceSizeZ, RC_Palette.Concrete, 0.35f);

            // Helipad ring — a landing mark reads instantly as "the VIP is here".
            RC_BuildUtil.Cylinder(t, "HelipadRing", TerraceCenter + new Vector3(-5f, 0.03f, 0f),
                6.2f, 0.06f, RC_Palette.Helipad, false);
            RC_BuildUtil.Cylinder(t, "HelipadInner", TerraceCenter + new Vector3(-5f, 0.05f, 0f),
                5.4f, 0.06f, RC_Palette.RoofTar, false);

            // Rooftop clutter. It doubles as partial cover the guards can drag the VIP behind,
            // so a slow second shot really can be denied.
            //
            // Nothing here is tall any more. Height is expensive on this terrace: the sight line
            // comes in at 6.8 degrees of depression, so an object throws a shadow of its own
            // height / tan(6.8deg) — about 8.4 metres of dead ground per metre of object. The
            // water tank that used to stand at (-11, -8) was 4.4m, which shadowed roughly 37m
            // on a terrace only 24m deep: a third of the venue was permanently un-shootable,
            // on the contract that is supposed to be the gentle one. It is gone. What is left
            // tops out at 3.2m and mostly sits at chest height, breaking up the silhouette
            // without deleting the shot.
            RC_BuildUtil.Box(t, "AC_A", TerraceCenter + new Vector3(9f, 0.9f, 7f), new Vector3(3.4f, 1.8f, 2.6f), RC_Palette.Metal);
            RC_BuildUtil.Box(t, "AC_B", TerraceCenter + new Vector3(11.5f, 0.7f, 2f), new Vector3(2.6f, 1.4f, 2.2f), RC_Palette.Metal);
            RC_BuildUtil.Box(t, "Stairhouse", TerraceCenter + new Vector3(-11f, 1.6f, 8.5f), new Vector3(5f, 3.2f, 4.2f), RC_Palette.Concrete);
            RC_BuildUtil.Box(t, "Crate_A", TerraceCenter + new Vector3(4f, 0.6f, -9f), new Vector3(1.6f, 1.2f, 1.6f), RC_Palette.RustMetal, 22f);

            // VIP patrol route — a lazy loop across the open half of the terrace.
            AddPoint(t, refs.vipWaypoints, "VIP_WP_A", TerraceCenter + new Vector3(-4f, 0f, 4f));
            AddPoint(t, refs.vipWaypoints, "VIP_WP_B", TerraceCenter + new Vector3(4f, 0f, -3f));
            AddPoint(t, refs.vipWaypoints, "VIP_WP_C", TerraceCenter + new Vector3(-8f, 0f, -6f));

            // Evac exits, one at each end of the terrace so the detail always has somewhere to
            // run that is away from the shooter.
            //
            // Note the stairhouse exit is NOT in cover: the nest looks along +Z, the stairhouse
            // sits at z 6.4..10.6, and this point is at 5.5 — in front of it, not behind. That
            // is the door side, so it is where a bodyguard would actually take him, and it
            // leaves the player one last shot at a running man. Deliberate, not an oversight.
            AddPoint(t, refs.vipEvacPoints, "VIP_EVAC_Stairs", TerraceCenter + new Vector3(-11f, 0f, 5.5f));
            // The second exit used to sit behind the water tank. With the tank gone it would be
            // a spot in the open named after an object that no longer exists, so it moved behind
            // the air handlers — 1.8m tall and 2.5m in front of it, which does shadow it.
            AddPoint(t, refs.vipEvacPoints, "VIP_EVAC_AC", TerraceCenter + new Vector3(10.5f, 0f, 9.5f));

            // Initial guard posts, loosely ringing the VIP. Six are laid out; a mission uses the
            // first protectorCount of them, so a heavier detail fills the ring rather than
            // stacking extra guards on the same spots.
            AddPoint(t, refs.guardPosts, "Post_1", TerraceCenter + new Vector3(-2f, 0f, -7f));
            AddPoint(t, refs.guardPosts, "Post_2", TerraceCenter + new Vector3(6f, 0f, 5f));
            AddPoint(t, refs.guardPosts, "Post_3", TerraceCenter + new Vector3(-9f, 0f, 0f));
            AddPoint(t, refs.guardPosts, "Post_4", TerraceCenter + new Vector3(2f, 0f, 8f));
            AddPoint(t, refs.guardPosts, "Post_5", TerraceCenter + new Vector3(9f, 0f, -4f));
            AddPoint(t, refs.guardPosts, "Post_6", TerraceCenter + new Vector3(-6f, 0f, -10f));

            // Bystanders on the far side of the terrace, well clear of the patrol loop.
            for (int i = 0; i < 3; i++)
                AddPoint(t, refs.civilianPosts, $"Civ_Post_{i}",
                    TerraceCenter + new Vector3(8f + i * 2.2f, 0f, -6f + i * 3f));

            refs.targetSpawn = TerraceCenter + new Vector3(-2f, 0f, 0f);
            refs.targetOneWayRoute = false;
            refs.targetEscapesAtEnd = false;

            RC_BuildUtil.MarkStaticRecursive(group);
            return group;
        }

        // ------------------------------------------------------------------ street venue

        /// <summary>
        /// Contract 2 — the target arrives by motorcade at the end of the boulevard and walks
        /// off along the far pavement, crossing the sight line.
        ///
        /// The road is empty when the mission opens. Three cars — black lead and rear, the
        /// target's own between them — come in from behind the right-hand wall along the far
        /// lanes of the cross street and pull up at the far kerb on the left of the view:
        /// guards out of the lead car, the target out of the middle one, more guards out of the
        /// rear one. The arrival *is* the "where does he come from" — the player watches it
        /// happen down the one road the map points at.
        ///
        /// Once out he walks right, back the way the cars came, and is gone when the right-hand
        /// wall closes over him. He goes into nothing — the wall is the deadline, and the player
        /// can see exactly how much pavement is left. Every metre of the walk is pure lateral
        /// motion against a player looking down Z, so the shot has to be led — at 300m the round
        /// is in the air for 0.75s and a 1.2 m/s walk has moved him most of a body width by the
        /// time it arrives. Stopping the column at the far left of the mouth gives that walk
        /// about 48m, some 40 seconds in view.
        ///
        ///   하차 x −16.4 ──── 인도 (z = +1.5, 표적 진행 방향 →) ──── x +28 벽 뒤로 사라짐 · x +32 이탈
        ///   ─────────────────── 연석 z −2.1 ────────────────────────────────────
        ///   [앞차 −24] [표적 차 −17] [뒤차 −9.5]   ◀── 진입 (x +32 에서 출발)   z −4.75 차로
        ///   ─────────────────── 중앙선 z −10 ───────────────────────────────────
        ///                                 맞은편 2차로
        ///   ─────────────────── 인도 z −21..−17 (대로 입구 좌우) ──────────────────
        /// </summary>
        static GameObject BuildStreetVenue(Transform root, MapRefs refs)
        {
            var group = RC_BuildUtil.Empty(root, "TargetStreet", TerraceCenter);
            var t = group.transform;

            const float walkZ = 1.5f;        // the pavement line the target walks
            const float stopLaneZ = -4.75f;  // centre of the kerbside lane the motorcade stops in
            // From the nest the right-hand wall closes over the walk line at about x +28. The
            // end of the walk is a few metres past that, so he is out of sight before he counts
            // as gone.
            const float exitX = 32f;

            // Pavement. Raised 0.18 so it reads as a kerb from 300m and, more importantly, so
            // the NavMesh bake has a surface of its own rather than trying to walk the ground
            // slab that runs under the entire city. Wider than the boulevard's mouth (about
            // ±27m from the nest) so its ends never show.
            var walk = RC_BuildUtil.Box(t, "Surface", TerraceCenter + new Vector3(0f, 0.09f, walkZ),
                new Vector3(80f, 0.18f, 7f), RC_Palette.Concrete);
            refs.terraceSurface = walk.transform;
            RC_BuildUtil.Box(t, "Kerb", TerraceCenter + new Vector3(0f, 0.09f, walkZ - 3.6f),
                new Vector3(80f, 0.2f, 0.5f), RC_Palette.ConcreteDark, 0f, false);

            // The frontage closing off the far side of the street. Behind him from the player's
            // view, so it is also the backdrop that makes him legible. There is no lobby and no
            // bus shelter any more: he walks into nothing, and nothing on the pavement hides him.
            RC_BuildUtil.Box(t, "Frontage", TerraceCenter + new Vector3(0f, 7f, walkZ + 8.5f),
                new Vector3(80f, 14f, 10f), RC_Palette.Concrete);

            // Street trees on the kerb line. Canopies at head height are the fiddliest occluder
            // in the mission — they hide the head but not the body, which is exactly the shot
            // the player does not want to take. The trunks stand clear of the stop lane.
            foreach (float tx in new[] { -28f, -9f, 7f })
            {
                RC_BuildUtil.Cylinder(t, "Tree_Trunk", TerraceCenter + new Vector3(tx, 1.6f, -2.3f),
                    0.22f, 3.2f, RC_Palette.RustMetal);
                RC_BuildUtil.Cylinder(t, "Tree_Canopy", TerraceCenter + new Vector3(tx, 3.6f, -2.3f),
                    1.7f, 1.6f, RC_Palette.Foliage, false);
            }

            BuildCrossStreet(root);

            // ---- the motorcade -------------------------------------------------------------
            //
            // Built at the stops so the scene reads correctly in the editor; MotorcadeArrival
            // rolls it back out of view at the start and drives it in. Parented to the map root,
            // not the venue: the NavMesh bake would turn three cars into walkable islands, and
            // the venue is marked static, which a moving car must not be.
            //
            // Heading −X in the kerbside lane on the far side, so the doors on the right open
            // onto the pavement.
            var motorcade = RC_BuildUtil.Empty(root, "Motorcade").transform;
            const float carYaw = -90f;
            // Stopped at the far left of the mouth, which shows the stop lane to about x ±27.6:
            // the lead car's nose (x −26.5) is just inside it. The rear car is the 6.5m military
            // vehicle, so it sits 7.5m back rather than 7.
            refs.motorcadeCars = new[]
            {
                AddMotorcadeCar(motorcade, "Car_Lead", "Classic Car_9",
                    TerraceCenter + new Vector3(-24f, 0.05f, stopLaneZ), carYaw, true),
                AddMotorcadeCar(motorcade, "Car_Principal", "N_Muscle Car_10",
                    TerraceCenter + new Vector3(-17f, 0.05f, stopLaneZ), carYaw, false),
                AddMotorcadeCar(motorcade, "Car_Rear", "Military Vehicle_3",
                    TerraceCenter + new Vector3(-9.5f, 0.05f, stopLaneZ), carYaw, true),
            };
            refs.motorcadeHeading = Vector3.left;
            // From x +32 to −24 for the lead car, so the whole column — the rear car starts at
            // +46.5 — sets off hidden behind the right-hand wall.
            refs.motorcadeApproach = 56f;
            refs.motorcadeCue = "차량 3대 접근 중. 우측 차로에서 들어온다.";

            // ---- route ----------------------------------------------------------------
            float y = 0.18f;
            // Out of the principal car's rear door, onto the walking line, then right until the
            // wall takes him.
            AddPoint(t, refs.vipWaypoints, "VIP_WP_Start", TerraceCenter + new Vector3(-16.4f, y, -1.0f));
            AddPoint(t, refs.vipWaypoints, "VIP_WP_Mid1", TerraceCenter + new Vector3(-8f, y, walkZ));
            AddPoint(t, refs.vipWaypoints, "VIP_WP_Mid2", TerraceCenter + new Vector3(14f, y, walkZ));
            AddPoint(t, refs.vipWaypoints, "VIP_WP_Exit", TerraceCenter + new Vector3(exitX, y, walkZ));

            // What the player is being told to look at, in the order the walk presents it. The
            // first fires the moment he is out of the car (RouteNarrator announces a waypoint on
            // departure); the motorcade's own approach line comes from MotorcadeArrival.
            refs.routeCues.Add("정차. 중간 차량에서 표적 하차 확인.");
            refs.routeCues.Add("표적이 인도를 따라 우측으로 이동한다.");
            refs.routeCues.Add("표적이 우측 건물 뒤로 빠지기 직전이다.");
            refs.routeCues.Add("표적이 시야에서 사라졌다. 임무 실패.");

            AddPoint(t, refs.vipEvacPoints, "VIP_EVAC_Exit", TerraceCenter + new Vector3(exitX, y, walkZ));
            // Back to the car he came in — on the pavement beside it, not out in the lane: the
            // evac goal is fed straight to a NavMeshAgent, and the road is not baked.
            AddPoint(t, refs.vipEvacPoints, "VIP_EVAC_Motorcade", TerraceCenter + new Vector3(-17f, y, -1.4f));

            // Guards stand where they get out: beside the lead car's doors (0) and the rear
            // car's (2). A mission uses the first protectorCount posts, so the order alternates
            // ends of the column — four guards is two out of each car, not four out of one.
            AddGuardPost(t, refs, "Post_1", new Vector3(-24.8f, y, -1.0f), 0);
            AddGuardPost(t, refs, "Post_2", new Vector3(-23.4f, y, -0.6f), 0);
            AddGuardPost(t, refs, "Post_3", new Vector3(-10.3f, y, -1.0f), 2);
            AddGuardPost(t, refs, "Post_4", new Vector3(-8.9f, y, -0.6f), 2);
            AddGuardPost(t, refs, "Post_5", new Vector3(-9.6f, y, 0.2f), 2);
            AddGuardPost(t, refs, "Post_6", new Vector3(-24.1f, y, 0.2f), 0);

            for (int i = 0; i < 3; i++)
                AddPoint(t, refs.civilianPosts, $"Civ_Post_{i}",
                    TerraceCenter + new Vector3(14f - i * 9f, y, walkZ + 2.4f));

            refs.targetSpawn = refs.vipWaypoints[0].position;
            refs.targetOneWayRoute = true;
            refs.targetEscapesAtEnd = true;

            RC_BuildUtil.MarkStaticRecursive(group);
            return group;
        }

        static void AddGuardPost(Transform parent, MapRefs refs, string name, Vector3 offset, int car)
        {
            AddPoint(parent, refs.guardPosts, name, TerraceCenter + offset);
            refs.guardPostCar.Add(car);
        }

        /// <summary>
        /// Dresses the cross street as a two-way, four-lane road: centre line, lane lines, and a
        /// pavement on the near side either side of the boulevard's mouth (the far pavement is
        /// the venue's own). Not under the venue — none of this is anywhere anyone walks, and
        /// the NavMesh bake treats every mesh under the venue as ground.
        /// </summary>
        static void BuildCrossStreet(Transform root)
        {
            var group = RC_BuildUtil.Empty(root, "CrossStreet");
            var t = group.transform;

            const float halfLength = 60f;    // well past the ±27m the boulevard's mouth shows
            float centreZ = (CrossStreetNearZ + CrossStreetFarZ) * 0.5f;
            float laneWidth = (CrossStreetFarZ - CrossStreetNearZ) / 4f;
            // On top of Road_H (top face at y 0.05).
            const float markY = 0.06f, markH = 0.02f;

            var yellow = RC_Palette.Get("RC_RoadYellow", new Color(0.86f, 0.68f, 0.12f), 0.3f);
            var white = RC_Palette.Get("RC_RoadWhite", new Color(0.88f, 0.88f, 0.86f), 0.3f);

            // Double yellow centre line.
            foreach (float dz in new[] { -0.14f, 0.14f })
                RC_BuildUtil.Box(t, "Line_Centre", new Vector3(0f, markY, centreZ + dz),
                    new Vector3(halfLength * 2f, markH, 0.15f), yellow, 0f, false);

            // Dashed lane lines between the two lanes on each side: 3m dash, 5m gap.
            foreach (float lz in new[] { CrossStreetNearZ + laneWidth, CrossStreetFarZ - laneWidth })
                for (float x = -halfLength; x < halfLength; x += 8f)
                    RC_BuildUtil.Box(t, "Line_Lane", new Vector3(x + 1.5f, markY, lz),
                        new Vector3(3f, markH, 0.15f), white, 0f, false);

            // Near pavement, either side of the T-junction.
            float pavementDepth = CrossStreetNearZ - CrossStreetNearPavementZ;
            float pavementZ = (CrossStreetNearZ + CrossStreetNearPavementZ) * 0.5f;
            float pavementLength = halfLength - BoulevardHalfWidth;
            foreach (float side in new[] { -1f, 1f })
            {
                RC_BuildUtil.Box(t, side < 0 ? "NearPavementL" : "NearPavementR",
                    new Vector3(side * (BoulevardHalfWidth + pavementLength * 0.5f), 0.09f, pavementZ),
                    new Vector3(pavementLength, 0.18f, pavementDepth), RC_Palette.Concrete, 0f, false);
                RC_BuildUtil.Box(t, side < 0 ? "NearKerbL" : "NearKerbR",
                    new Vector3(side * (BoulevardHalfWidth + pavementLength * 0.5f), 0.09f, CrossStreetNearZ - 0.25f),
                    new Vector3(pavementLength, 0.2f, 0.5f), RC_Palette.ConcreteDark, 0f, false);
            }

            RC_BuildUtil.MarkStaticRecursive(group);
        }

        const string MotorcadeCarFolder = "Assets/Awbmecreations/Mobile Optimize-Free Low Poly Cars/Prefabs";

        /// <summary>
        /// One motorcade car, at its stop. The Awbme models are built in metres with the nose
        /// on +Z (their FR tyre sits at +X, +Z), so they go in at native scale and yaw −90
        /// points them down −X. Placed by measured bounds, so the car's centre is on the stop
        /// and its wheels are on the road whatever the prefab's pivot.
        /// </summary>
        static Transform AddMotorcadeCar(Transform parent, string name, string model, Vector3 stop,
            float yaw, bool paintBlack)
        {
            var rot = Quaternion.Euler(0f, yaw, 0f);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{MotorcadeCarFolder}/{model}.prefab");
            if (prefab == null)
            {
                Debug.LogWarning($"[Rooftop Contract] {model} not found in {MotorcadeCarFolder} — box stand-in.");
                var stand = RC_BuildUtil.Box(parent, name, stop + Vector3.up * 0.75f,
                    new Vector3(1.9f, 1.5f, 4.8f), RC_Palette.DoorDark, yaw);
                return stand.transform;
            }

            Bounds b = RC_BuildUtil.MeasurePrefab(prefab);
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
            go.name = name;
            Vector3 centre = rot * new Vector3(b.center.x, 0f, b.center.z);
            go.transform.SetPositionAndRotation(new Vector3(stop.x - centre.x, stop.y - b.min.y, stop.z - centre.z), rot);

            if (paintBlack) RC_CarPaint.PaintBodyBlack(go, model);

            // It moves, so its collider gets a kinematic body rather than being a static
            // collider shoved around every frame.
            var body = go.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;

            Debug.Log($"[Rooftop Contract] {name} = {model}: {b.size.x:0.00} x {b.size.y:0.00} x {b.size.z:0.00} m (W x H x L)");
            return go.transform;
        }

        // ------------------------------------------------------------------ helipad venue

        /// <summary>
        /// Contract 3 — the target walking to a helicopter that is already turning.
        ///
        /// This is the only mission with a clock, and the clock is diegetic: the deadline is a
        /// man crossing forty metres of open helipad, and the player can see exactly how much
        /// of it is left. Three rounds, five bodyguards walling him in, and the pad is deliberately
        /// bare — there is no clutter to deny a shot, only the wall of bodies.
        ///
        /// The helicopter is parented to the map root, NOT to this venue. The NavMesh bake walks
        /// every MeshFilter under the venue transform and calls it ground; a helicopter in that
        /// hierarchy bakes as a walkable island and punches a hole in the pad. This exact bug
        /// already cost four rounds of debugging on the exfil bird — see 00_HANDOFF.md §6.
        /// </summary>
        static GameObject BuildHelipadVenue(Transform root, MapRefs refs)
        {
            var group = RC_BuildUtil.Empty(root, "TargetHelipad", TerraceCenter);
            var t = group.transform;

            const float padX = 12f;          // pad centre, the end of the walk
            const float doorX = -13f;        // stairhouse door, the start of it

            var floor = RC_BuildUtil.Box(t, "Surface", TerraceCenter + Vector3.down * 0.25f,
                new Vector3(TerraceSizeX + 8f, 0.5f, TerraceSizeZ), RC_Palette.RoofTar);
            refs.terraceSurface = floor.transform;

            // Same 0.35m kerb on the nest-facing side as the meeting terrace: a full parapet
            // would shadow the floor and this venue has nothing else to hide behind.
            BuildParapet(t, TerraceCenter, TerraceSizeX + 8f, TerraceSizeZ, RC_Palette.Concrete, 0.35f);

            RC_BuildUtil.Cylinder(t, "HelipadRing", TerraceCenter + new Vector3(padX, 0.03f, 0f),
                7.0f, 0.06f, RC_Palette.Helipad, false);
            RC_BuildUtil.Cylinder(t, "HelipadInner", TerraceCenter + new Vector3(padX, 0.05f, 0f),
                6.2f, 0.06f, RC_Palette.RoofTar, false);

            // Stairhouse he comes out of, at the far end from the pad.
            RC_BuildUtil.Box(t, "Stairhouse", TerraceCenter + new Vector3(doorX - 3f, 1.7f, 6f),
                new Vector3(6f, 3.4f, 4.4f), RC_Palette.Concrete);
            RC_BuildUtil.Box(t, "Stairhouse_Door", TerraceCenter + new Vector3(doorX - 3f, 1.2f, 3.75f),
                new Vector3(1.8f, 2.4f, 0.16f), RC_Palette.DoorDark, 0f, false);

            // The only two objects on the walk. Both are short — they break the silhouette,
            // they do not deny the shot.
            RC_BuildUtil.Box(t, "AC_A", TerraceCenter + new Vector3(-2f, 0.7f, 8.5f),
                new Vector3(3.0f, 1.4f, 2.2f), RC_Palette.Metal);
            RC_BuildUtil.Box(t, "Crate_A", TerraceCenter + new Vector3(2.5f, 0.55f, -9f),
                new Vector3(1.6f, 1.1f, 1.6f), RC_Palette.RustMetal, 22f);

            // ---- the bird ------------------------------------------------------------
            // Root-parented on purpose. See the summary.
            var heliPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(HeliPrefab);
            if (heliPrefab != null)
            {
                var heli = (GameObject)PrefabUtility.InstantiatePrefab(heliPrefab, root);
                heli.name = "TargetHeli";
                heli.transform.position = TerraceCenter + new Vector3(padX, 0.05f, 0f);
                heli.transform.rotation = Quaternion.Euler(0f, 205f, 0f);
                foreach (var c in heli.GetComponentsInChildren<Collider>()) Object.DestroyImmediate(c);
                var idle = heli.AddComponent<HeliRotorSpin>();
                idle.rotors = FindHeliRotors(heli);
            }
            else
            {
                Debug.LogWarning($"[Rooftop Contract] 헬기 프리팹 없음 — 헬리패드 미션의 표적 헬기 생략: {HeliPrefab}");
            }

            // ---- route ----------------------------------------------------------------
            // Four points across forty metres. The pause at each is what makes the walk read as
            // a man being escorted rather than a man fleeing, and it is also the player's shot.
            AddPoint(t, refs.vipWaypoints, "VIP_WP_Door", TerraceCenter + new Vector3(doorX, 0f, 3.0f));
            AddPoint(t, refs.vipWaypoints, "VIP_WP_Cross", TerraceCenter + new Vector3(-4f, 0f, 1.0f));
            AddPoint(t, refs.vipWaypoints, "VIP_WP_PadEdge", TerraceCenter + new Vector3(5.5f, 0f, 0.5f));
            AddPoint(t, refs.vipWaypoints, "VIP_WP_Board", TerraceCenter + new Vector3(padX - 2.5f, 0f, 0f));

            refs.routeCues.Add("표적이 계단실에서 나왔다. 헬리패드까지 40m.");
            refs.routeCues.Add("헬리패드 횡단 중. 경호원 5명이 밀착해 사격 각도가 좁다.");
            refs.routeCues.Add("헬기 탑승 직전이다. 지금 제거해야 한다.");
            refs.routeCues.Add("표적이 헬기에 탑승했다. 임무 실패.");

            // Both exits are away from the nest and behind something solid.
            AddPoint(t, refs.vipEvacPoints, "VIP_EVAC_Stairs", TerraceCenter + new Vector3(doorX - 3f, 0f, 4.4f));
            AddPoint(t, refs.vipEvacPoints, "VIP_EVAC_Heli", TerraceCenter + new Vector3(padX - 1f, 0f, -1.5f));

            // Five of these get used. Tight, and biased to the nest-facing side: this detail is
            // walling him off from the city, and they know which direction the city is.
            AddPoint(t, refs.guardPosts, "Post_1", TerraceCenter + new Vector3(doorX + 2.5f, 0f, 0.5f));
            AddPoint(t, refs.guardPosts, "Post_2", TerraceCenter + new Vector3(doorX - 1.5f, 0f, -1.5f));
            AddPoint(t, refs.guardPosts, "Post_3", TerraceCenter + new Vector3(doorX + 1f, 0f, 5.5f));
            AddPoint(t, refs.guardPosts, "Post_4", TerraceCenter + new Vector3(doorX + 5f, 0f, -2.5f));
            AddPoint(t, refs.guardPosts, "Post_5", TerraceCenter + new Vector3(doorX - 4f, 0f, 2.5f));
            AddPoint(t, refs.guardPosts, "Post_6", TerraceCenter + new Vector3(padX - 6f, 0f, -3.5f));

            for (int i = 0; i < 2; i++)
                AddPoint(t, refs.civilianPosts, $"Civ_Post_{i}",
                    TerraceCenter + new Vector3(padX + 1f - i * 4f, 0f, 7.5f + i * 1.5f));

            refs.targetSpawn = TerraceCenter + new Vector3(doorX - 3f, 0f, 3.4f);
            refs.targetOneWayRoute = true;
            refs.targetEscapesAtEnd = true;

            RC_BuildUtil.MarkStaticRecursive(group);
            return group;
        }

        public const string HeliPrefab = "Assets/Low Poly Helicopters Pack Free/Prefabs/Heli_2.prefab";

        /// <summary>Heli_2 is a coaxial: two counter-rotating discs named Rotor_Twin_Upper/Lower.</summary>
        static Transform[] FindHeliRotors(GameObject heli)
        {
            var found = new List<Transform>();
            foreach (var child in heli.GetComponentsInChildren<Transform>(true))
                if (child.name.StartsWith("Rotor")) found.Add(child);
            return found.ToArray();
        }

        // ------------------------------------------------------------------ player nest

        /// <summary>
        /// The player's roof, built as a mechanical plant deck.
        ///
        /// The sniping half (front, +Z) is unchanged — low parapet, cover wall, crates. The
        /// defense half behind it is the actual level design: one door at the far end and three
        /// rows of machinery between it and the player, each row further forward and each with
        /// fewer places to stand.
        ///
        ///   z −18.4   문 (계단실). 모든 돌격대가 여기서 나온다.
        ///   z −13.7   STAGE 1 — 냉각탑 뱅크. 5자리. 문에서 첫 도약, 플레이어까지 19 m.
        ///   z −7.9    STAGE 2 — 공조기 + 덕트. 4자리. 양옆은 완전 엄폐, 가운데는 허리 높이.
        ///   z −2.6    STAGE 3 — 실외기 한 쌍. 2자리뿐. 플레이어까지 8 m.
        ///   z +4      플레이어 엄폐벽 (양끝 7 m 는 열려 있다 = 최종 돌입 통로)
        ///
        /// The slot counts are the difficulty curve. Two slots at stage 3 means two attackers
        /// close, ever — the rest are stuck behind the chillers waiting for one of them to die.
        /// The cooling towers are tall enough to hide the doorway completely, which is what lets
        /// the spawn point sit in the open without anybody popping into existence on screen.
        ///
        /// Two rules govern the plant geometry, and breaking either one makes the fight read as
        /// mush no matter how good the AI is:
        ///
        ///   1. Cover is at most ~3.4m wide, and every slot's peek step is derived from that
        ///      width (see AddCover). Wide cover cannot be leaned out of.
        ///   2. The player's centre sight line (x = 0, from z = +5.3) is clear from stage 3 all
        ///      the way to the cooling towers. Only the door screen at stage 1 sits on it.
        /// </summary>
        static GameObject BuildNest(Transform root, MapRefs refs)
        {
            var group = RC_BuildUtil.Empty(root, "PlayerNest", NestCenter);
            var t = group.transform;

            var floor = RC_BuildUtil.Box(t, "Surface", Deck + Vector3.down * 0.25f,
                new Vector3(NestSizeX, 0.5f, NestSizeZ), RC_Palette.RoofTar);
            refs.nestSurface = floor.transform;

            float halfX = NestSizeX * 0.5f;
            float frontZ = NestCenter.z + 10f;      // unchanged: the sniping edge
            float backZ = Deck.z - NestSizeZ * 0.5f;
            var mat = RC_Palette.Concrete;

            // Low on purpose. The player has to look down 6.8 degrees to reach the terrace;
            // a chest-high wall 1.6m in front of the eye would clip that line entirely.
            RC_BuildUtil.Box(t, "Parapet_Front", new Vector3(NestCenter.x, NestCenter.y + 0.275f, frontZ),
                new Vector3(NestSizeX, 0.55f, ParapetThickness), mat);
            RC_BuildUtil.Box(t, "Parapet_Back", new Vector3(NestCenter.x, NestCenter.y + ParapetHeight * 0.5f, backZ),
                new Vector3(NestSizeX, ParapetHeight, ParapetThickness), mat);
            RC_BuildUtil.Box(t, "Parapet_Left", Deck + new Vector3(-halfX, ParapetHeight * 0.5f, 0f),
                new Vector3(ParapetThickness, ParapetHeight, NestSizeZ), mat);
            RC_BuildUtil.Box(t, "Parapet_Right", Deck + new Vector3(halfX, ParapetHeight * 0.5f, 0f),
                new Vector3(ParapetThickness, ParapetHeight, NestSizeZ), mat);

            // ---- the player's own cover line -----------------------------------------
            //
            // Deliberately narrower than the roof: the 7m gaps at either end are the flanking
            // routes, which is both what makes sliding along the wall with A/D worth doing and
            // where a final push comes from.
            RC_BuildUtil.Box(t, "CoverWall", NestCenter + new Vector3(0f, 0.62f, 4.0f),
                new Vector3(13f, 1.24f, 0.7f), RC_Palette.ConcreteDark);
            RC_BuildUtil.Box(t, "CoverCrate_L", NestCenter + new Vector3(-5.5f, 0.55f, 7.4f),
                new Vector3(1.8f, 1.1f, 1.8f), RC_Palette.RustMetal, 18f);
            RC_BuildUtil.Box(t, "CoverCrate_R", NestCenter + new Vector3(5.8f, 0.55f, 7.6f),
                new Vector3(1.8f, 1.1f, 1.8f), RC_Palette.RustMetal, -12f);

            BuildStairwell(t, refs);
            BuildApproachStage1(t, refs);
            BuildApproachStage2(t, refs);
            BuildApproachStage3(t, refs);

            // Shooting pose anchors. Sniping happens at the front parapet facing +Z;
            // the defense fight happens two metres back, facing -Z behind the cover wall.
            // Pitched to put the venue under the crosshair on spawn — computed, because a
            // constant tuned for one contract points at the sky in the next one.
            var nestAnchor = RC_BuildUtil.Empty(t, "SniperNest",
                NestCenter + new Vector3(0f, 1.15f, 8.4f), Quaternion.Euler(SpawnPitch, 0f, 0f));
            refs.sniperNest = nestAnchor.transform;

            var cover = RC_BuildUtil.Empty(t, "CoverAnchor",
                NestCenter + new Vector3(0f, 0.85f, 5.3f), Quaternion.Euler(0f, 180f, 0f));
            refs.coverAnchor = cover.transform;

            RC_BuildUtil.MarkStaticRecursive(group);
            // The anchors must not be static — the player camera is driven from them.
            GameObjectUtility.SetStaticEditorFlags(nestAnchor, 0);
            GameObjectUtility.SetStaticEditorFlags(cover, 0);
            if (refs.assaultDoorLight != null)
                GameObjectUtility.SetStaticEditorFlags(refs.assaultDoorLight.gameObject, 0);
            return group;
        }

        // ------------------------------------------------------------------ the door

        /// <summary>
        /// The stairwell head. One door, at the back of the roof, and every attacker in the game
        /// comes through it.
        ///
        /// A single entrance is worth more than three scattered spawns: the player can learn it,
        /// watch it, and pre-aim it, and the fight stops feeling like enemies materialising
        /// behind them. It sits 4 m behind the first row of cooling towers, which hide it
        /// completely from the player's eye line — so nobody is ever seen appearing out of
        /// nothing, they are seen coming out from behind the plant.
        /// </summary>
        static void BuildStairwell(Transform t, MapRefs refs)
        {
            Vector3 house = NestCenter + new Vector3(0f, 1.9f, -21.6f);
            RC_BuildUtil.Box(t, "Stairwell", house, new Vector3(7f, 3.8f, 4.5f), RC_Palette.Concrete);
            RC_BuildUtil.Box(t, "Stairwell_Cap", house + new Vector3(0f, 2.0f, 0f),
                new Vector3(7.6f, 0.22f, 5.0f), RC_Palette.ConcreteDark, 0f, false);

            // Face of the house is at z = -19.35. The door sits just proud of it so it reads as
            // an opening rather than a decal, and carries no collider — the doorway is a hole.
            float face = NestCenter.z - 19.35f;
            RC_BuildUtil.Box(t, "Door_Frame", new Vector3(NestCenter.x, NestCenter.y + 1.22f, face + 0.06f),
                new Vector3(2.1f, 2.5f, 0.12f), RC_Palette.Metal, 0f, false);
            RC_BuildUtil.Box(t, "Door_Opening", new Vector3(NestCenter.x, NestCenter.y + 1.12f, face + 0.14f),
                new Vector3(1.75f, 2.25f, 0.16f), RC_Palette.DoorDark, 0f, false);
            RC_BuildUtil.Box(t, "Door_Lamp", new Vector3(NestCenter.x, NestCenter.y + 2.62f, face + 0.2f),
                new Vector3(0.5f, 0.22f, 0.32f), RC_Palette.Metal, 0f, false);

            var lampGo = RC_BuildUtil.Empty(t, "Door_Light",
                new Vector3(NestCenter.x, NestCenter.y + 2.45f, face + 0.6f));
            var lamp = lampGo.AddComponent<Light>();
            lamp.type = LightType.Point;
            lamp.color = new Color(1f, 0.86f, 0.66f);
            lamp.range = 9f;
            lamp.intensity = 1.2f;
            lamp.shadows = LightShadows.None;
            refs.assaultDoorLight = lamp;

            // Just outside the threshold, facing the roof. The director jitters sideways from
            // here so a stack of three does not come out along one exact line.
            AddPoint(t, refs.assaultSpawns, "Spawn_Door", NestCenter + new Vector3(0f, 0f, -18.4f));
        }

        // ------------------------------------------------------------------ approach stages

        /// <summary>
        /// Stage 1 — the cooling tower bank. Tall, hard, and wide enough to hide the doorway.
        /// Five slots, more than the live cap, so the door never jams behind a full stage.
        ///
        /// Widths matter more than positions here. A slot's peek step is derived from the width
        /// of the thing it hides behind (see <see cref="AddCover"/>), and a 3.6m unit needs a
        /// 2.3m step to clear — which only exists if the lane beside it is wider than that.
        /// Every unit is therefore ~3m with ≥2m of open deck on both sides.
        ///
        /// CoolTower_B stays centred on x = 0 on purpose: it is the screen that hides the
        /// doorway, so nobody is ever seen appearing out of nothing.
        /// </summary>
        static void BuildApproachStage1(Transform t, MapRefs refs)
        {
            PlantUnit(t, "CoolTower_A", NestCenter + new Vector3(-6.6f, 0f, -11.0f), new Vector3(3.0f, 3.5f, 3.2f), true);
            PlantUnit(t, "CoolTower_B", NestCenter + new Vector3(0.0f, 0f, -11.0f), new Vector3(3.4f, 3.5f, 3.2f), true);
            PlantUnit(t, "CoolTower_C", NestCenter + new Vector3(6.4f, 0f, -11.0f), new Vector3(3.0f, 3.5f, 3.2f), true);

            // Flank condenser banks — lower, and they sit further back so the lanes past the
            // towers stay open.
            PlantUnit(t, "CondBank_L", NestCenter + new Vector3(-11.4f, 0f, -12.6f), new Vector3(2.6f, 2.0f, 2.0f), false);
            PlantUnit(t, "CondBank_R", NestCenter + new Vector3(11.2f, 0f, -12.6f), new Vector3(2.6f, 2.0f, 2.0f), false);

            AddCover(t, refs, "S1_A", new Vector3(-6.6f, 0f, -13.7f), 1, true, 3.0f);
            AddCover(t, refs, "S1_B", new Vector3(0.0f, 0f, -13.7f), 1, true, 3.4f);
            AddCover(t, refs, "S1_C", new Vector3(6.4f, 0f, -13.7f), 1, true, 3.0f);
            AddCover(t, refs, "S1_D", new Vector3(-11.4f, 0f, -14.9f), 1, true, 2.6f);
            AddCover(t, refs, "S1_E", new Vector3(11.2f, 0f, -14.9f), 1, true, 2.6f);
        }

        /// <summary>
        /// Stage 2 — air handlers on the flanks, a duct run down the middle.
        ///
        /// The asymmetry is the point. Both flanks are full-height cover; the centre is a duct at
        /// waist height, so anyone taking the short route through the middle is visible from the
        /// chest up. The player who watches the centre lane gets rewarded for it.
        ///
        /// That last sentence used to be a lie. The duct's top face sat at y = 1.70, and the
        /// player's eye when peeking sits at exactly 1.70 (cover anchor 0.85 + peek 0.85) — the
        /// sight line grazed the duct, so the "exposed" centre attacker was a sliver that ate
        /// bullets on the duct lip. The top face is now 1.25, which puts a standing attacker's
        /// chest and head clearly above it while still hiding them from a player who has ducked.
        /// </summary>
        static void BuildApproachStage2(Transform t, MapRefs refs)
        {
            // 5.0m wide with one slot in the middle was unpeekable — no sideways step short of
            // 2.9m cleared the casing. 3.2m wide is peekable and still full cover to stand behind.
            PlantUnit(t, "AHU_L", NestCenter + new Vector3(-8.4f, 0f, -5.6f), new Vector3(3.2f, 2.4f, 3.0f), false);
            PlantUnit(t, "AHU_R", NestCenter + new Vector3(8.2f, 0f, -5.6f), new Vector3(3.2f, 2.4f, 3.0f), false);

            // Duct on stands, like every rooftop in the reference photographs.
            // Centre y 0.80 with a 0.9 section = top face at 1.25. See the summary above.
            RC_BuildUtil.Box(t, "Duct_Run", NestCenter + new Vector3(0f, 0.8f, -5.6f),
                new Vector3(7f, 0.9f, 1.5f), RC_Palette.Plant);
            // The elbow used to sit at x = 3.2, right in the near-centre firing lane. Pushed out
            // to the edge of the duct run so the lane between duct and AHU_R stays clean.
            RC_BuildUtil.Box(t, "Duct_Elbow", NestCenter + new Vector3(4.6f, 0.8f, -4.4f),
                new Vector3(1.4f, 0.9f, 2.6f), RC_Palette.Plant);
            foreach (float lx in new[] { -3.0f, 3.0f })
                foreach (float lz in new[] { -0.55f, 0.55f })
                    RC_BuildUtil.Box(t, "Duct_Leg", NestCenter + new Vector3(lx, 0.18f, -5.6f + lz),
                        new Vector3(0.22f, 0.35f, 0.22f), RC_Palette.Metal, 0f, false);

            AddCover(t, refs, "S2_A", new Vector3(-8.4f, 0f, -7.9f), 2, true, 3.2f);
            AddCover(t, refs, "S2_B", new Vector3(8.2f, 0f, -7.9f), 2, true, 3.2f);
            // Soft cover: these two are meant to be shootable. The step out is short on purpose —
            // the duct never hides them anyway, and a wide step would walk them into the AHUs.
            AddCover(t, refs, "S2_C", new Vector3(-2.4f, 0f, -7.4f), 2, false, 1.0f);
            AddCover(t, refs, "S2_D", new Vector3(2.4f, 0f, -7.4f), 2, false, 1.0f);
        }

        /// <summary>
        /// Stage 3 — a pair of condensers seven metres out, and nothing else. Two slots is the
        /// hard cap on how many attackers can ever be close, no matter how the fight is going.
        ///
        /// "And nothing else" is now literally true. A 2.3m vent stack used to stand at x = 0,
        /// 5.5m dead ahead of the cover anchor: the single most important sight line in the
        /// defense phase ran straight into a pillar. It has been moved to the left flank, where
        /// it is scenery instead of a blindfold.
        /// </summary>
        static void BuildApproachStage3(Transform t, MapRefs refs)
        {
            PlantUnit(t, "Condenser_L", NestCenter + new Vector3(-5.0f, 0f, -0.7f), new Vector3(2.4f, 2.1f, 1.5f), true);
            PlantUnit(t, "Condenser_R", NestCenter + new Vector3(5.2f, 0f, -0.7f), new Vector3(2.4f, 2.1f, 1.5f), true);
            // Left flank, clear of the push corridor (the cover wall's open ends are |x| > 6.5).
            RC_BuildUtil.Cylinder(t, "VentStack", NestCenter + new Vector3(-12.4f, 1.15f, 0.6f),
                0.7f, 2.3f, RC_Palette.RustMetal);

            AddCover(t, refs, "S3_L", new Vector3(-5.0f, 0f, -2.6f), 3, true, 2.4f);
            AddCover(t, refs, "S3_R", new Vector3(5.2f, 0f, -2.6f), 3, true, 2.4f);
        }

        /// <summary>
        /// One piece of rooftop plant: a painted casing, a coil grille on the face the player
        /// sees, and optionally the fan ring on top that makes it read as HVAC rather than as a
        /// crate. Only the casing carries a collider — the trim would just complicate the bake.
        /// </summary>
        static void PlantUnit(Transform t, string name, Vector3 groundPos, Vector3 size, bool fan)
        {
            Vector3 centre = groundPos + Vector3.up * (size.y * 0.5f);
            RC_BuildUtil.Box(t, name, centre, size, RC_Palette.Plant);

            // Coil face towards the player (+Z), inset so it does not z-fight the casing.
            RC_BuildUtil.Box(t, name + "_Coil",
                centre + new Vector3(0f, -size.y * 0.08f, size.z * 0.5f + 0.03f),
                new Vector3(size.x * 0.84f, size.y * 0.66f, 0.06f), RC_Palette.Coil, 0f, false);

            if (!fan) return;
            float top = groundPos.y + size.y;
            float radius = Mathf.Min(size.x, size.z) * 0.36f;
            RC_BuildUtil.Cylinder(t, name + "_FanRing", new Vector3(centre.x, top + 0.11f, centre.z),
                radius, 0.22f, RC_Palette.Metal, false);
            RC_BuildUtil.Cylinder(t, name + "_FanHub", new Vector3(centre.x, top + 0.2f, centre.z),
                radius * 0.34f, 0.16f, RC_Palette.ConcreteDark, false);
        }

        /// <summary>
        /// A place to stand and not be shot. Left facing +Z (towards the player) so the peek
        /// step, which runs along the point's own right axis, slides along the face of the cover
        /// rather than into it.
        ///
        /// <paramref name="coverWidth"/> is the width of the object this slot hides behind, and
        /// it is not optional flavour — it is the fix for the single worst bug in the defense
        /// phase. Every slot used to inherit CoverPoint's default 1.15m peek step while hiding
        /// behind objects 2.4m to 5.0m wide, so "leaning out to shoot" left the attacker still
        /// geometrically behind their own cover: the player saw a sliver of a body and every
        /// round hit the casing. Stepping half the width plus a shoulder actually clears it.
        /// </summary>
        static void AddCover(Transform t, MapRefs refs, string name, Vector3 offset, int stage, bool hard,
                             float coverWidth)
        {
            var go = RC_BuildUtil.Empty(t, name, NestCenter + offset);
            var cp = go.AddComponent<CoverPoint>();
            cp.stage = stage;
            cp.hardCover = hard;
            cp.peekOffset = coverWidth * 0.5f + ShoulderClearance;
            refs.assaultCover.Add(cp);
        }

        // ------------------------------------------------------------------ (nest end)

        // ------------------------------------------------------------------ helpers

        static void BuildParapet(Transform parent, Vector3 center, float sx, float sz, Material mat,
                                 float southHeight = ParapetHeight)
        {
            float hx = sx * 0.5f, hz = sz * 0.5f;
            RC_BuildUtil.Box(parent, "Parapet_N", center + new Vector3(0f, ParapetHeight * 0.5f, hz),
                new Vector3(sx, ParapetHeight, ParapetThickness), mat);
            RC_BuildUtil.Box(parent, "Parapet_S", center + new Vector3(0f, southHeight * 0.5f, -hz),
                new Vector3(sx, southHeight, ParapetThickness), mat);
            RC_BuildUtil.Box(parent, "Parapet_W", center + new Vector3(-hx, ParapetHeight * 0.5f, 0f),
                new Vector3(ParapetThickness, ParapetHeight, sz), mat);
            RC_BuildUtil.Box(parent, "Parapet_E", center + new Vector3(hx, ParapetHeight * 0.5f, 0f),
                new Vector3(ParapetThickness, ParapetHeight, sz), mat);
        }

        static void Retexture(GameObject go, Material mat)
        {
            if (mat == null) return;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                var mats = new Material[r.sharedMaterials.Length == 0 ? 1 : r.sharedMaterials.Length];
                for (int i = 0; i < mats.Length; i++) mats[i] = mat;
                r.sharedMaterials = mats;
            }
        }

        static void AddPoint(Transform parent, List<Transform> into, string name, Vector3 pos)
        {
            var go = RC_BuildUtil.Empty(parent, name, pos);
            into.Add(go.transform);
        }
    }
}
