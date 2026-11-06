using System.Collections.Generic;
using System.IO;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

namespace RooftopContract.EditorTools
{
    /// <summary>
    /// One button that produces a playable scene: layers, map, lighting, actors, NavMesh, HUD.
    /// Everything is rebuilt from scratch each run, so the scene is disposable and the
    /// generator is the source of truth.
    ///
    /// Which characters get cast comes from CastingConfig, not from hardcoded paths, so
    /// swapping in your own MagicaVoxel models never requires touching this file.
    /// </summary>
    public static partial class RC_SceneBuilder
    {
        public const string CastingPath = "Assets/_Game/CastingConfig.asset";
        public const string MissionSetPath = "Assets/_Game/MissionSet.asset";
        /// <summary>
        /// How long the target stays frozen after the shield has finished closing, before he
        /// breaks for an exit. Small on purpose — it is a beat, not a pause.
        /// </summary>
        const float ShieldHoldMargin = 0.15f;

        public const string WeaponFolder = "Assets/Low Poly Weapon Pack 4_MW_1/Prefabs/Weapons/Weapons_PreSet";
        public const string PistolWeaponFolder = "Assets/Low Poly Pistol Weapon Pack 2/Prefabs/Weapons";

        static CastingConfig casting;

        [MenuItem("Rooftop Contract/캐스팅 설정 (CastingConfig)", priority = -79)]
        public static void OpenCasting()
        {
            var cfg = LoadOrCreateCasting();
            Selection.activeObject = cfg;
            EditorGUIUtility.PingObject(cfg);
        }

        [MenuItem("Rooftop Contract/계약 설정 (MissionSet)", priority = -80)]
        public static void OpenMissionSet()
        {
            var set = LoadOrCreateMissionSet();
            Selection.activeObject = set;
            EditorGUIUtility.PingObject(set);
        }

        [MenuItem("Rooftop Contract/도구/무기 — 플레이어 소총 배치 저장", priority = 66)]
        public static void SavePlayerRiflePlacement()
        {
            if (EditorApplication.isPlaying)
            {
                EditorUtility.DisplayDialog("Exit Play Mode",
                    "Play Mode에서는 총이 난간 고정용으로 분리됩니다. Play를 끄고 Scene 뷰에서 배치하세요.", "OK");
                return;
            }

            GameObject rifle = Selection.activeGameObject;
            if (rifle == null || rifle.name != "Rifle")
                rifle = GameObject.Find("Rifle");

            if (rifle == null)
            {
                EditorUtility.DisplayDialog("Rifle not found",
                    "Mission 씬에서 Player/PlayerCamera/WeaponHolder/Rifle 을 선택한 뒤 다시 실행하세요.", "OK");
                return;
            }

            string sceneName = EditorSceneManager.GetActiveScene().name;
            if (!sceneName.StartsWith("Mission") ||
                !int.TryParse(sceneName.Substring("Mission".Length), out int missionNumber))
            {
                EditorUtility.DisplayDialog("Mission scene required",
                    "Mission1, Mission2 또는 Mission3 씬에서 저장하세요.", "OK");
                return;
            }

            var set = LoadOrCreateMissionSet();
            int missionIndex = missionNumber - 1;
            var mission = set.Get(missionIndex);
            if (mission == null)
            {
                EditorUtility.DisplayDialog("Mission not found",
                    $"MissionSet에 Mission {missionNumber} 데이터가 없습니다.", "OK");
                return;
            }

            Undo.RecordObject(set, $"Save Mission {missionNumber} Rifle Placement");
            mission.playerRifleLocalPosition = rifle.transform.localPosition;
            mission.playerRifleLocalEuler = rifle.transform.localEulerAngles;
            EditorUtility.SetDirty(set);
            AssetDatabase.SaveAssets();
            Debug.Log($"[Rooftop Contract] Mission {missionNumber} 저격총 배치 저장 — " +
                      $"위치 {mission.playerRifleLocalPosition:F3}, 회전 {mission.playerRifleLocalEuler:F1}");
            Selection.activeObject = set;
            EditorGUIUtility.PingObject(set);
        }

        /// <summary>
        /// The only button anyone should need.
        ///
        /// The build used to be "menu 1, then 6, then 3, and 0b if you added a character" — four
        /// items, in an order you had to remember, where forgetting menu 1 got you a magenta
        /// helicopter and forgetting 6 got you a brown blob at 250m. None of that was a decision
        /// the person pressing the buttons was making; it was a dependency order the tool knew
        /// and was refusing to act on.
        ///
        /// So it acts on it. The two asset passes are idempotent — they skip anything already
        /// converted, so running them every build costs a folder scan — and casting is only
        /// auto-assigned when a role is actually empty, so a hand-picked cast is never
        /// overwritten. The numbered items all still exist for when one step needs running on
        /// its own; this is what you press otherwise.
        /// </summary>
        [MenuItem("Rooftop Contract/★ BUILD EVERYTHING (전체 재생성)", priority = -100)]
        public static void BuildEverything()
        {
            var set = LoadOrCreateMissionSet();
            if (set.Count == 0)
            {
                EditorUtility.DisplayDialog("No missions", "MissionSet 이 비어 있습니다.", "OK");
                return;
            }

            if (!EditorUtility.DisplayDialog("Build Everything",
                "에셋 준비 → 캐스팅 확인 → 씬 생성까지 전부 실행합니다.\n\n" +
                "1. 머티리얼 URP 변환 (이미 된 건 건너뜀)\n" +
                "2. 팔레트 텍스처 수정 (이미 된 건 건너뜀)\n" +
                "3. 비어 있는 배역만 자동 배정\n" +
                $"4. 사무실 + 미션 {set.Count}개 씬 생성 (덮어쓰기) + NavMesh 베이크\n\n" +
                "끝나면 Office 씬이 열립니다.", "Build", "Cancel")) return;

            EnsureLayers();
            EnsureFolders();

            // Asset prep. Both of these are no-ops once they have run, which is the whole reason
            // they can live inside the build instead of in the user's head.
            RC_MaterialUpgrade.Run();
            RC_VoxelTextureFixer.FixAll();

            if (!PrepareCasting(autoAssign: true)) return;
            BuildScenes(set);
        }

        [MenuItem("Rooftop Contract/도구/씬 — 미션 전체 재생성 (에셋 준비 없이)", priority = 2)]
        public static void BuildAllMissions()
        {
            if (!PrepareCasting(autoAssign: false)) return;

            var set = LoadOrCreateMissionSet();
            if (set.Count == 0)
            {
                EditorUtility.DisplayDialog("No missions", "MissionSet 이 비어 있습니다.", "OK");
                return;
            }

            if (!EditorUtility.DisplayDialog("Build All Missions",
                $"사무실 + {set.Count}개 미션 씬을 생성(덮어쓰기)합니다.\n\n" +
                "Assets/_Game/Scenes/Office.unity\n" +
                "Assets/_Game/Scenes/Mission1..N.unity\n\nContinue?", "Build", "Cancel")) return;

            EnsureLayers();
            EnsureFolders();
            BuildScenes(set);
        }

        /// <summary>
        /// Makes sure every role has a character behind it, filling empty ones from the folder
        /// names when asked. It never touches a role that already has somebody in it — a cast
        /// picked by hand outranks one picked by string matching.
        /// </summary>
        static bool PrepareCasting(bool autoAssign)
        {
            casting = LoadOrCreateCasting();
            if (casting.IsComplete) return true;

            if (autoAssign)
            {
                RC_AutoCast.AutoAssign();
                casting = LoadOrCreateCasting();
                if (casting.IsComplete) return true;
            }

            EditorUtility.DisplayDialog("Casting incomplete",
                $"CastingConfig 에 아직 비어 있는 역할이 있습니다:\n\n{casting.MissingRoles()}\n\n" +
                "폴더명 자동 배정으로도 채우지 못했습니다. " +
                "Assets/VoxelCharacter/ 아래 폴더 이름과 프리팹을 확인해 주세요.", "OK");
            Selection.activeObject = casting;
            EditorGUIUtility.PingObject(casting);
            return false;
        }

        /// <summary>Generates every scene the game ships with, then opens the one it starts in.</summary>
        static void BuildScenes(MissionSet set)
        {
            for (int i = 0; i < set.Count; i++)
                BuildOneMission(set, i);

            // Built after the missions because the briefing copies its numbers out of the same
            // contracts, and because it is the scene the campaign actually starts in.
            BuildOffice(set);

            AddScenesToBuildSettings(set);

            EditorSceneManager.OpenScene(MissionSet.OfficeScenePath);
            Selection.activeObject = AssetDatabase.LoadAssetAtPath<SceneAsset>(MissionSet.OfficeScenePath);
            Debug.Log($"[Rooftop Contract] ✅ 빌드 완료 — 사무실 + 미션 {set.Count}개. " +
                      "Office 씬을 열었습니다. Press Play.");
        }

        static void BuildOneMission(MissionSet set, int index)
        {
            var mission = set.Get(index);
            string scenePath = MissionSet.ScenePathFor(index);

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            BuildLighting(index);
            var map = RC_MapBuilder.Build(mission);

            var director = new GameObject("MissionDirector").AddComponent<MissionDirector>();
            director.missionIndex = index;
            director.missionCount = set.Count;
            director.codename = mission.codename;
            director.brief = mission.brief;
            director.strikeWindow = mission.strikeWindow;
            director.assaultDelay = mission.assaultDelay;
            director.survivalDuration = mission.survivalDuration;

            var player = BuildPlayer(map, mission);
            if (player != null && player.sniper != null)
            {
                player.sniper.magazine = mission.sniperAmmo;

                // Zero the rifle for this contract. The straight-line distance is what the
                // bullet actually flies, so a shot 58m down at 300m out is zeroed on 305.6m,
                // not 300m. Baked here rather than read at runtime so the value is visible in
                // the inspector next to the ammo count it belongs with.
                player.sniper.zeroRange = Mathf.Sqrt(
                    mission.engagementRange * mission.engagementRange +
                    mission.elevationAdvantage * mission.elevationAdvantage);
            }

            controllerCache.Clear();
            var vipTarget = BuildProtectionDetail(map, mission, index);
            BuildAssault(map, mission);
            BuildExtraction(map);
            BuildHud(player, vipTarget);
            BuildAudio();

            // NavMeshSurface writes its baked data next to the scene file, so the scene has to
            // exist on disk before the bake. Save, bake, save again.
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, scenePath);

            BuildNavMesh(scenePath, map);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, scenePath);

            Debug.Log($"[Rooftop Contract] Mission {index + 1} '{mission.codename}' — " +
                      $"{mission.engagementRange:0}m / +{mission.elevationAdvantage:0}m, " +
                      $"{mission.sniperAmmo}발, 경호원 {mission.protectorCount}명 → {scenePath}");
        }

        static void BuildAudio()
        {
            var audio = new GameObject("GameAudio").AddComponent<GameAudio>();
            audio.sniperShot = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Sound/RifleShotSound.wav");
            audio.sniperAction = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Sound/lever-action-rifle.wav");
            audio.empty = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Sound/empty-gun-shot.wav");
            audio.pistolShot = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Sound/PistolShot.wav");
            audio.pistolMagRemove = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Sound/mag_remove.wav");
            audio.pistolMagInsert = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Sound/mag_insert.wav");
            audio.pistolSlideReturn = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Sound/PistolSlidereturn.wav");
            audio.enemySmgShot = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Sound/SMGShot.wav");
            audio.enemyPistolShot = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Sound/enemy_pistolshot.wav");
            audio.cityAmbience = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Sound/city-ambience-distant-stereo.wav");
            audio.helicopterAmbience = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Sound/helicopter-ambience.wav");
            audio.tenseBackground = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Sound/tensebackgroundsound.wav");
            audio.metalImpacts = new[]
            {
                AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Sound/bullet_hits_Metal_1.wav"),
                AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Sound/bullet_hits_Metal_2.wav"),
                AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Sound/bullet_hits_Metal_3.wav")
            };
        }

        // ------------------------------------------------------------------ mission set

        public static MissionSet LoadOrCreateMissionSet()
        {
            var set = AssetDatabase.LoadAssetAtPath<MissionSet>(MissionSetPath);
            if (set == null)
            {
                EnsureFolders();
                set = ScriptableObject.CreateInstance<MissionSet>();
                set.missions = MissionSet.BuildDefaults();
                set.assetVersion = MissionSet.CurrentVersion;
                AssetDatabase.CreateAsset(set, MissionSetPath);
                Debug.Log($"[Rooftop Contract] Created {MissionSetPath} with 3 default contracts.");
            }
            else if (set.NeedsDefaultsRefresh)
            {
                // An asset written before the venue fields existed. Its three contracts all
                // claim to be rooftop terraces because that is the C# default, not because
                // anybody chose it — so the contracts are re-seeded from BuildDefaults. Target
                // casting survives, since that is the one thing the asset genuinely owns (the
                // auto-assign pass below re-fills anything missed).
                var keptVips = new List<GameObject>();
                for (int i = 0; i < set.Count; i++) keptVips.Add(set.Get(i).vipPrefab);

                set.missions = MissionSet.BuildDefaults();
                for (int i = 0; i < set.Count && i < keptVips.Count; i++)
                    set.Get(i).vipPrefab = keptVips[i];

                set.assetVersion = MissionSet.CurrentVersion;
                EditorUtility.SetDirty(set);
                AssetDatabase.SaveAssets();
                Debug.Log("[Rooftop Contract] MissionSet 을 새 계약 정의(도로/헬리패드 포함)로 갱신했습니다. " +
                          "인스펙터에서 손으로 맞춘 수치가 있었다면 다시 넣어 주세요.");
            }

            // One target per contract, taken in order from the casting pool. Assigning here
            // rather than by hand means adding a fourth VIP creates a fourth contract's target
            // without any extra wiring.
            bool changed = false;
            for (int i = 0; i < set.Count; i++)
            {
                var m = set.Get(i);
                if (m.vipPrefab != null) continue;
                var vip = CastingConfig.Pick(casting != null ? casting.vipPrefabs : null, i);
                if (vip != null) { m.vipPrefab = vip; changed = true; }
            }
            if (changed) { EditorUtility.SetDirty(set); AssetDatabase.SaveAssets(); }

            return set;
        }

        // ------------------------------------------------------------------ casting

        public static CastingConfig LoadOrCreateCasting()
        {
            var cfg = AssetDatabase.LoadAssetAtPath<CastingConfig>(CastingPath);
            if (cfg == null)
            {
                EnsureFolders();
                cfg = ScriptableObject.CreateInstance<CastingConfig>();
                AssetDatabase.CreateAsset(cfg, CastingPath);
                Debug.Log($"[Rooftop Contract] Created {CastingPath}. 캐릭터 프리팹을 여기에 할당하세요.");
            }

            // No stock-character fallback. An unassigned role is an error to report, never
            // something to quietly fill with somebody else's asset.
            return cfg;
        }

        // ------------------------------------------------------------------ setup

        static void EnsureLayers()
        {
            RC_BuildUtil.EnsureLayer(GameLayers.PlayerName);
            RC_BuildUtil.EnsureLayer(GameLayers.ActorName);
            RC_BuildUtil.EnsureLayer(GameLayers.HitboxName);
        }

        static void EnsureFolders()
        {
            if (!AssetDatabase.IsValidFolder("Assets/_Game")) AssetDatabase.CreateFolder("Assets", "_Game");
            foreach (var f in new[] { "Scenes", "Materials", "Animation", "Prefabs", "UI" })
                if (!AssetDatabase.IsValidFolder($"Assets/_Game/{f}"))
                    AssetDatabase.CreateFolder("Assets/_Game", f);
        }

        /// <summary>
        /// Mission scenes go first and in order. SceneManager.LoadScene(name) needs them in the
        /// build list, otherwise "next contract" silently does nothing in a player build.
        /// </summary>
        /// <summary>
        /// The office goes first. Build index 0 is what a built player boots into, and the
        /// campaign is supposed to start with a briefing, not on a roof mid-contract.
        /// </summary>
        static void AddScenesToBuildSettings(MissionSet set)
        {
            var ourPaths = new List<string> { MissionSet.OfficeScenePath };
            for (int i = 0; i < set.Count; i++) ourPaths.Add(MissionSet.ScenePathFor(i));

            var rest = new List<EditorBuildSettingsScene>();
            foreach (var s in EditorBuildSettings.scenes)
                if (!ourPaths.Contains(s.path)) rest.Add(s);

            var ordered = new List<EditorBuildSettingsScene>();
            foreach (var p in ourPaths) ordered.Add(new EditorBuildSettingsScene(p, true));
            ordered.AddRange(rest);

            EditorBuildSettings.scenes = ordered.ToArray();
        }

        // ------------------------------------------------------------------ lighting

        static void BuildLighting(int missionIndex)
        {
            var sunGo = new GameObject("Sun");
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            // Low magic-hour angle: long shadows across the roofs read the sniper line, and the
            // strong silhouette contrast keeps voxel characters legible at 250m.
            sunGo.transform.rotation = Quaternion.Euler(24f, 142f, 0f);
            sun.color = new Color(1f, 0.87f, 0.70f);
            sun.intensity = 1.85f;
            sun.shadows = LightShadows.Soft;

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.42f, 0.50f, 0.63f);
            RenderSettings.ambientEquatorColor = new Color(0.35f, 0.34f, 0.36f);
            RenderSettings.ambientGroundColor = new Color(0.16f, 0.15f, 0.16f);

            string skyboxPath = missionIndex switch
            {
                0 => "Packages/xyz.staggartcreations.skyboxes/Skyboxes/Sky_Sunset.mat",
                1 => "Packages/xyz.staggartcreations.skyboxes/Skyboxes/Sky_Dusk.mat",
                _ => "Packages/xyz.staggartcreations.skyboxes/Skyboxes/Sky_NightMoon.mat"
            };
            RenderSettings.skybox = AssetDatabase.LoadAssetAtPath<Material>(skyboxPath);

            // Distance haze does real work here: it separates the 250m target roof from the
            // skyline behind it, which is most of what sells the range.
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = new Color(0.62f, 0.66f, 0.73f);
            RenderSettings.fogDensity = 0.0016f;
        }

        // ------------------------------------------------------------------ player

        static PlayerRoot BuildPlayer(RC_MapBuilder.MapRefs map, MissionDefinition mission)
        {
            var root = new GameObject("Player");
            root.layer = GameLayers.Player;
            root.transform.position = map.coverAnchor != null ? map.coverAnchor.position : RC_MapBuilder.NestCenter;

            var health = root.AddComponent<Damageable>();
            health.maxHealth = 100f;

            var camGo = new GameObject("PlayerCamera");
            camGo.transform.SetParent(root.transform, false);
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.nearClipPlane = 0.03f;
            cam.farClipPlane = 1500f;
            cam.fieldOfView = 60f;
            camGo.AddComponent<AudioListener>();
            if (map.sniperNest != null)
                camGo.transform.SetPositionAndRotation(map.sniperNest.position, map.sniperNest.rotation);

            // Body volume that rides with the camera. Cover geometry blocks incoming raycasts
            // against this, so being behind the wall is protection in the literal sense.
            var body = new GameObject("PlayerBody") { layer = GameLayers.Player };
            body.transform.SetParent(camGo.transform, false);
            body.transform.localPosition = new Vector3(0f, -0.45f, 0f);
            var capsule = body.AddComponent<CapsuleCollider>();
            capsule.direction = 1;
            capsule.radius = 0.34f;
            capsule.height = 1.5f;
            capsule.isTrigger = true;
            var hb = body.AddComponent<Hitbox>();
            hb.owner = health;
            hb.damageMultiplier = 1f;

            var sniper = camGo.AddComponent<SniperRig>();
            var pistol = camGo.AddComponent<PistolRig>();
            pistol.aimCamera = cam;
            pistol.enabled = false;

            var holder = new GameObject("WeaponHolder");
            holder.transform.SetParent(camGo.transform, false);

            // The eye is 0.6m above the low front parapet. Keep the rifle low and forward so
            // its fore-end visually rests on that ledge instead of floating in front of the
            // player's chest. X stays slightly right to preserve a clear sight line at 1x.
            var rifle = AttachWeapon(holder.transform, $"{WeaponFolder}/Recon_P_PreSet.prefab", "Rifle",
                // Root stays behind and above the concrete; the bipod, which sits 0.57m
                // forward on this model, is the part that reaches the parapet cap.
                mission != null ? mission.playerRifleLocalPosition : new Vector3(0.10f, -0.499f, 1.156f));
            if (rifle != null && mission != null)
                rifle.transform.localEulerAngles = mission.playerRifleLocalEuler;
            var sidearm = AttachWeapon(holder.transform, $"{PistolWeaponFolder}/Pistol_J.prefab", "Pistol",
                new Vector3(0.24f, -0.20f, 0.34f));
            if (sidearm != null) sidearm.SetActive(false);

            var vm = holder.AddComponent<WeaponViewModel>();
            vm.sniper = sniper;
            vm.pistol = pistol;
            vm.restPosition = Vector3.zero;

            if (rifle != null)
            {
                var muzzle = new GameObject("Muzzle").transform;
                muzzle.SetParent(rifle.transform, false);
                muzzle.localPosition = new Vector3(0f, 0f, 1.1f);
                sniper.muzzle = muzzle;

                var mounted = rifle.AddComponent<MountedRifleView>();
                mounted.sniper = sniper;
                mounted.aimCamera = cam;
                mounted.bipod = FindChildExact(rifle.transform, "Recon_P_Bipod");
            }
            if (sidearm != null)
            {
                var muzzle = new GameObject("Muzzle").transform;
                muzzle.SetParent(sidearm.transform, false);
                muzzle.localPosition = new Vector3(0f, 0f, 0.3f);
                pistol.muzzle = muzzle;

                var pistolView = sidearm.AddComponent<PistolViewModel>();
                pistolView.pistol = pistol;
                pistolView.slide = FindChildExact(sidearm.transform, "Pistol_J_BarrelGuard");
                pistolView.slideFollower = FindChildExact(sidearm.transform, "Pistol_J_Sight");
                pistolView.magazine = FindChildContains(sidearm.transform, "_Mag");
            }

            var playerRoot = root.AddComponent<PlayerRoot>();
            playerRoot.playerCamera = cam;
            playerRoot.sniper = sniper;
            playerRoot.pistol = pistol;
            playerRoot.sniperNest = map.sniperNest;
            playerRoot.coverAnchor = map.coverAnchor;
            playerRoot.sniperModel = rifle;
            playerRoot.pistolModel = sidearm;
            playerRoot.health = health;

            var intro = root.AddComponent<PlayerIntro>();
            intro.playerCamera = cam;
            intro.sniper = sniper;
            intro.sniperNest = map.sniperNest;
            intro.weaponHolder = holder.transform;
            intro.rifle = rifle != null ? rifle.transform : null;
            playerRoot.intro = intro;

            return playerRoot;
        }

        static Transform FindChildContains(Transform root, string token)
        {
            if (root == null) return null;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t != root && t.name.IndexOf(token, System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return t;
            return null;
        }

        static Transform FindChildExact(Transform root, string objectName)
        {
            if (root == null) return null;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t != root && t.name == objectName) return t;
            return null;
        }

        static GameObject AttachWeapon(Transform parent, string path, string name, Vector3 localPos)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) { Debug.LogWarning($"[Rooftop Contract] Weapon missing: {path}"); return null; }

            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
            go.name = name;
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            foreach (var c in go.GetComponentsInChildren<Collider>()) Object.DestroyImmediate(c);
            return go;
        }

        // ------------------------------------------------------------------ target detail

        /// <summary>
        /// The face on the dossier and the face in the scope have to be the same one, so both
        /// the mission generator and the office briefing resolve the target through here.
        ///
        /// The fallback used to be <c>Pick(vipPrefabs, 0)</c> for every contract, which quietly
        /// cast the same man as all three targets — invisible while nobody looked at a photo of
        /// him, and obvious the moment the briefing put one on the desk.
        /// </summary>
        public static GameObject VipPrefabFor(MissionDefinition mission, int missionIndex)
        {
            if (mission != null && mission.vipPrefab != null) return mission.vipPrefab;
            return CastingConfig.Pick(casting.vipPrefabs, missionIndex);
        }

        static VipTarget BuildProtectionDetail(RC_MapBuilder.MapRefs map, MissionDefinition mission, int missionIndexForCasting)
        {
            var detailGo = new GameObject("ProtectionDetail");
            detailGo.transform.position = RC_MapBuilder.TerraceCenter;

            var vipPrefab = VipPrefabFor(mission, missionIndexForCasting);
            var vipActor = SpawnActor(vipPrefab, "VIP_Target",
                map.targetSpawn, detailGo.transform,
                casting.vipHealth, ActorRole.Vip);
            var vip = vipActor.AddComponent<VipTarget>();
            vip.calmWaypoints = map.vipWaypoints.ToArray();
            vip.evacPoints = map.vipEvacPoints.ToArray();
            // Set by the venue, not by the mission: a man on a terrace loops a patrol, a man on
            // a pavement or a helipad is going somewhere and stops being shootable when he
            // arrives. The venue is the only thing that knows which of those it built.
            vip.oneWayRoute = map.targetOneWayRoute;
            vip.escapesAtRouteEnd = map.targetEscapesAtEnd;
            // A walking target does not stand around at each waypoint — the pauses are what
            // makes the patrol read as a patrol, and what makes a walk read as loitering.
            if (map.targetOneWayRoute) vip.waypointPause = 0.35f;

            // The target holds still until the shield around him has actually closed, and only
            // then breaks for the exit. Derived from shieldTightenTime rather than tuned on its
            // own, because the two numbers being independent is what produced the bug this
            // fixes: at 1.2s frozen against a 1.8s shield, he started running while the wall was
            // still open, so the one window the player had was a moving target AND a closing
            // gap at the same time. Coupled, the beat reads in two clean halves —
            //
            //   holding still, shield still open  ->  the shot
            //   shield shut, breaking for cover   ->  too late
            //
            // and the second bolt round (fires at 1.20s, lands at 1.83s over 250m) arrives
            // inside the first half instead of 0.03s after it ended.
            vip.terrifiedDuration = mission.shieldTightenTime + ShieldHoldMargin;

            // Radio traffic for the walk, if the venue wrote any. A contract whose target stands
            // still gets no narrator — a HUD that talks when nothing is happening teaches the
            // player to stop reading it.
            if (map.routeCues.Count > 0)
            {
                var narrator = vipActor.AddComponent<RouteNarrator>();
                narrator.target = vip;
                narrator.cues = map.routeCues.ToArray();
            }

            var detail = detailGo.AddComponent<ProtectionDetail>();
            // A target who is going somewhere gets an escort that goes with him; a target
            // standing at a meeting gets guards standing around. Same flag the venue already
            // set to say which kind of target it built, so the two can never disagree.
            detail.escortWhileCalm = map.targetOneWayRoute;
            detail.vip = vip;
            detail.shieldTightenTime = mission.shieldTightenTime;

            int guardCount = Mathf.Clamp(mission.protectorCount, 1, map.guardPosts.Count);
            var guardActors = new List<GameObject>();
            for (int i = 0; i < guardCount; i++)
            {
                var post = map.guardPosts[i];
                // Alternate through the pool so a two-type detail reads as two types, not four
                // clones of whichever prefab happened to be first.
                var guardPrefab = CastingConfig.Pick(casting.protectorPrefabs, i);
                var guardActor = SpawnActor(guardPrefab, $"Protector_{i + 1}",
                    post.position, detailGo.transform, casting.protectorHealth);
                guardActors.Add(guardActor);
                var guard = guardActor.AddComponent<ProtectorGuard>();
                bool usesPistol = (i % 2) == 1;
                guard.usesPistol = usesPistol;
                detail.guards.Add(guard);

                // Exactly one guard on the detail is allowed to slack off. Spread across four
                // people it would read as the whole detail being asleep; on one it reads as
                // that guy.
                var variety = guardActor.AddComponent<GuardIdleVariety>();
                variety.allowSlackIdle = (i == 1);

                string weaponPath = usesPistol
                    ? $"{PistolWeaponFolder}/Pistol_I.prefab"
                    : $"{WeaponFolder}/SMG_P_PreSet.prefab";
                AttachGuardWeapon(guardActor, weaponPath, usesPistol);
            }

            // Civilians: no mechanical cost, but they turn an 8x scan into surveillance
            // rather than a shooting range.
            if (casting.civilianPrefabs != null)
            {
                int civSlots = Mathf.Min(casting.civilianPrefabs.Length, map.civilianPosts.Count);
                for (int i = 0; i < civSlots; i++)
                {
                    if (casting.civilianPrefabs[i] == null) continue;
                    var pos = map.civilianPosts[i].position;
                    var civ = SpawnActor(casting.civilianPrefabs[i], $"Civilian_{i}",
                        pos, detailGo.transform, casting.civilianHealth, ActorRole.Civilian, addAgent: false);
                    civ.transform.rotation = Quaternion.Euler(0f, 190f + i * 25f, 0f);
                }
            }

            if (map.motorcadeCars != null && map.motorcadeCars.Length > 0)
                BuildMotorcadeArrival(map, detail, vip, guardActors);

            return vip;
        }

        /// <summary>
        /// Puts the detail into the venue's motorcade: the guards ride in the car their post
        /// belongs to, the target in the principal car, and they get out front to back.
        /// </summary>
        static void BuildMotorcadeArrival(RC_MapBuilder.MapRefs map, ProtectionDetail detail, VipTarget vip,
            List<GameObject> guardActors)
        {
            const int principalCar = 1;
            var passengers = new List<GameObject>();
            for (int car = 0; car < map.motorcadeCars.Length; car++)
            {
                for (int i = 0; i < guardActors.Count; i++)
                    if (i < map.guardPostCar.Count && map.guardPostCar[i] == car)
                        passengers.Add(guardActors[i]);
                if (car == principalCar) passengers.Add(vip.gameObject);
            }

            var arrival = map.motorcadeCars[0].parent.gameObject.AddComponent<MotorcadeArrival>();
            arrival.cars = map.motorcadeCars;
            arrival.passengers = passengers.ToArray();
            arrival.detail = detail;
            arrival.vip = vip;
            arrival.heading = map.motorcadeHeading;
            arrival.approachDistance = map.motorcadeApproach;
            arrival.approachCue = map.motorcadeCue;

            detail.holdForArrival = true;
        }

        static void AttachGuardWeapon(GameObject actor, string path, bool pistol)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) return;

            var setup = actor.GetComponent<ActorSetup>();
            if (setup != null) { setup.ResolveAnchors(); setup.Measure(); }
            bool hasAnchor = setup != null && setup.RightHand != null;
            Transform hand = hasAnchor ? setup.RightHand : actor.transform;

            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, hand);
            go.name = "Weapon";
            foreach (var c in go.GetComponentsInChildren<Collider>()) Object.DestroyImmediate(c);

            if (hasAnchor)
            {
                // In automatic mode WeaponGrip solves the placement at runtime, once the idle
                // animation has posed the arm — at edit time the model is in its T-pose with the
                // hands out sideways, so nothing about the *pose* can be measured here.
                var grip = go.AddComponent<WeaponGrip>();
                grip.actorRoot = actor.transform;
                grip.gripOffset = casting.weaponGripOffset;
                grip.extraEuler = casting.weaponExtraEuler;
                grip.targetLength = pistol ? casting.protectorPistolLength : casting.smgLength;
                grip.manual = pistol ? casting.protectorPistolGripManual : casting.weaponGripManual;
                grip.handOffset = pistol ? casting.protectorPistolHandOffset : casting.weaponHandOffset;
                grip.handEuler = pistol ? casting.protectorPistolHandEuler : casting.weaponHandEuler;

                // Solve once here regardless of mode. In manual mode that is the final answer —
                // a hand-authored grip is relative to the hand bone and does not need a posed
                // skeleton — so the component can go and the weapon is correct in the Scene view
                // as well. In automatic mode it is only a T-pose approximation that the runtime
                // solve replaces, but it means the saved scene never contains a weapon at its
                // raw prefab size, which is what made the first frame of Play flash a cannon.
                grip.Solve();
                if (grip.manual) Object.DestroyImmediate(grip);

                AttachSupportIK(actor, go,
                    pistol ? casting.protectorPistolLeftHandIK : casting.leftHandIK,
                    pistol ? casting.protectorPistolSupportCaptured : casting.weaponSupportCaptured,
                    pistol ? casting.protectorPistolSupportOffset : casting.weaponSupportOffset,
                    pistol ? casting.protectorPistolSupportEuler : casting.weaponSupportEuler);
            }
            else
            {
                // Unrigged mesh: hold it out at chest height so the guard still reads as armed.
                float h = setup != null ? setup.ActorHeight : casting.targetHeight;
                go.transform.localPosition = new Vector3(0.28f, h * casting.weaponFallbackHeightFraction, 0.3f);
                go.transform.localRotation = Quaternion.identity;
            }
        }

        /// <summary>
        /// The support-hand goal. Lives on the Animator's GameObject because that is the only
        /// place OnAnimatorIK is delivered — not on the actor root, where every other component
        /// of this actor sits.
        /// </summary>
        static void AttachSupportIK(GameObject actor, GameObject weapon, bool enabled, bool captured,
                                    Vector3 supportOffset, Vector3 supportEuler)
        {
            if (!enabled || !captured) return;

            var animator = actor.GetComponentInChildren<Animator>();
            if (animator == null || animator.avatar == null || !animator.avatar.isHuman) return;

            var ik = animator.gameObject.AddComponent<WeaponSupportIK>();
            ik.weapon = weapon.transform;
            ik.supportOffset = supportOffset;
            ik.supportEuler = supportEuler;
            ik.captured = captured;
            ik.positionWeight = casting.leftHandIKPositionWeight;
            ik.rotationWeight = casting.leftHandIKRotationWeight;
        }

        // ------------------------------------------------------------------ exfil

        // Same airframe as the one parked on the helipad venue, kept in one place so the two
        // helicopters in mission 3 cannot drift into being different models.
        public const string HeliPrefab = RC_MapBuilder.HeliPrefab;

        /// <summary>
        /// The extraction helicopter, parked outside the level until the survival clock expires.
        ///
        /// Built at the scene root, never under the nest. The NavMesh bake walks every MeshFilter
        /// under the rooftop transform and treats it as ground — a helicopter parented there
        /// would be baked in as a walkable island hanging over the city, and worse, its approach
        /// position would carve a hole in the deck the assault has to cross.
        /// </summary>
        static void BuildExtraction(RC_MapBuilder.MapRefs map)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(HeliPrefab);
            if (prefab == null)
            {
                Debug.LogWarning($"[Rooftop Contract] 탈출 헬기 프리팹을 찾지 못했습니다: {HeliPrefab}");
                return;
            }

            var root = new GameObject("Extraction");
            var rig = root.AddComponent<HeliExtraction>();

            var heli = (GameObject)PrefabUtility.InstantiatePrefab(prefab, root.transform);
            heli.name = "ExfilHeli";
            // Colliders would let the player's own bullets and the guards' fire hit the bird as
            // it flies through the firefight.
            foreach (var c in heli.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(c);

            ScaleToLength(heli, prefab, 12f);

            // Hovers off the front edge, over the city — the opposite side of the roof from the
            // stairwell door, so looking at the exfil means turning your back on the assault.
            var hover = new GameObject("Heli_Hover").transform;
            hover.SetParent(root.transform, false);
            hover.position = RC_MapBuilder.NestCenter + new Vector3(3f, 3.4f, 18f);
            hover.rotation = Quaternion.Euler(0f, 250f, 0f);

            // Comes in low and wide from the front left, climbing — so it crosses the player's
            // view rather than growing from a dot on the horizon.
            var approach = new GameObject("Heli_Approach").transform;
            approach.SetParent(root.transform, false);
            approach.position = RC_MapBuilder.NestCenter + new Vector3(-120f, -16f, 130f);
            approach.rotation = Quaternion.Euler(0f, 130f, 0f);

            rig.heli = heli;
            rig.hoverPoint = hover;
            rig.approachPoint = approach;
            rig.rotors = FindRotors(heli);

            heli.SetActive(false);
        }

        /// <summary>Scales a prop so its longest measured axis matches a real-world length.</summary>
        static void ScaleToLength(GameObject instance, GameObject prefab, float metres)
        {
            Bounds b = RC_BuildUtil.MeasurePrefab(prefab);
            float longest = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
            if (longest < 0.01f) return;
            instance.transform.localScale = Vector3.one * (metres / longest);
        }

        /// <summary>
        /// Rotor transforms by name. Heli_2 is a coaxial — "Rotor_Twin_Upper" and
        /// "Rotor_Twin_Lower" — and HeliExtraction counter-rotates alternate entries, so the
        /// order this returns is the order they spin against each other in.
        /// </summary>
        static Transform[] FindRotors(GameObject heli)
        {
            var found = new List<Transform>();
            foreach (var t in heli.GetComponentsInChildren<Transform>(true))
                if (t.name.ToLowerInvariant().Contains("rotor")) found.Add(t);

            if (found.Count == 0)
                Debug.LogWarning("[Rooftop Contract] 헬기에서 로터를 찾지 못했습니다 — 날개가 안 돕니다.");
            return found.ToArray();
        }

        // ------------------------------------------------------------------ assault wave

        static void BuildAssault(RC_MapBuilder.MapRefs map, MissionDefinition mission)
        {
            // One disabled template per assault type, parked below the map and cloned at runtime.
            // Keeping them in the scene avoids a prefab round-trip for a generated level.
            var templates = new List<GameObject>();
            int typeCount = 0;
            foreach (var p in casting.assaultPrefabs ?? new GameObject[0]) if (p != null) typeCount++;

            for (int i = 0; i < typeCount; i++)
            {
                var prefab = CastingConfig.Pick(casting.assaultPrefabs, i);
                var template = SpawnActor(prefab, $"AssaultGuardTemplate_{i + 1}",
                    RC_MapBuilder.NestCenter + new Vector3(i * 3f, -200f, 0f), null,
                    casting.assaultHealth);
                template.AddComponent<AssaultGuard>();
                template.SetActive(false);
                templates.Add(template);
            }

            var go = new GameObject("AssaultDirector");
            var dir = go.AddComponent<AssaultDirector>();
            dir.guardPrefabs = templates.ToArray();
            dir.spawnPoints = map.assaultSpawns.ToArray();
            // The staged approach lives entirely in these two references: the cover slots the
            // map laid out, and the lamp over the door that flashes when it opens.
            dir.coverPoints = map.assaultCover.ToArray();
            dir.doorLight = map.assaultDoorLight;
            dir.smgModel = AssetDatabase.LoadAssetAtPath<GameObject>($"{WeaponFolder}/AR_U_PreSet.prefab");
            dir.pistolModel = AssetDatabase.LoadAssetAtPath<GameObject>($"{PistolWeaponFolder}/Pistol_H.prefab");
            dir.weaponGripOffset = casting.weaponGripOffset;
            dir.weaponExtraEuler = casting.weaponExtraEuler;
            dir.smgLength = casting.smgLength;
            dir.pistolLength = casting.pistolLength;
            dir.weaponGripManual = casting.weaponGripManual;
            dir.weaponHandOffset = casting.weaponHandOffset;
            dir.weaponHandEuler = casting.weaponHandEuler;
            dir.leftHandIK = casting.leftHandIK;
            dir.leftHandIKPositionWeight = casting.leftHandIKPositionWeight;
            dir.leftHandIKRotationWeight = casting.leftHandIKRotationWeight;
            dir.weaponSupportCaptured = casting.weaponSupportCaptured;
            dir.weaponSupportOffset = casting.weaponSupportOffset;
            dir.weaponSupportEuler = casting.weaponSupportEuler;
            dir.pistolGripManual = casting.pistolGripManual;
            dir.pistolHandOffset = casting.pistolHandOffset;
            dir.pistolHandEuler = casting.pistolHandEuler;
            dir.pistolLeftHandIK = casting.pistolLeftHandIK;
            dir.pistolSupportCaptured = casting.pistolSupportCaptured;
            dir.pistolSupportOffset = casting.pistolSupportOffset;
            dir.pistolSupportEuler = casting.pistolSupportEuler;
            dir.assaultPrimaryGripManual = casting.assaultPrimaryGripManual;
            dir.assaultPrimaryHandOffset = casting.assaultPrimaryHandOffset;
            dir.assaultPrimaryHandEuler = casting.assaultPrimaryHandEuler;
            dir.assaultPrimaryLeftHandIK = casting.assaultPrimaryLeftHandIK;
            dir.assaultPrimarySupportCaptured = casting.assaultPrimarySupportCaptured;
            dir.assaultPrimarySupportOffset = casting.assaultPrimarySupportOffset;
            dir.assaultPrimarySupportEuler = casting.assaultPrimarySupportEuler;
            dir.assaultPrimaryLength = casting.assaultPrimaryLength;
            dir.weaponFallbackHeightFraction = casting.weaponFallbackHeightFraction;
            dir.maxAlive = mission.assaultMaxAlive;
            dir.lateGameMaxAlive = mission.assaultMaxAlive + 2;
            dir.spawnInterval = mission.assaultSpawnInterval;
        }

        // ------------------------------------------------------------------ actors

        /// <summary>
        /// Builds an actor as a logic root with the character model as a child. The split lets
        /// the model be rescaled, grounded and yaw-corrected independently of the AI, which is
        /// what makes an arbitrary MagicaVoxel export drop in without hand-fixing its transform.
        /// </summary>
        static GameObject SpawnActor(GameObject prefab, string name, Vector3 position, Transform parent,
                                     float health, ActorRole role = ActorRole.Guard, bool addAgent = true)
        {
            var root = new GameObject(name);
            if (parent != null) root.transform.SetParent(parent, false);
            root.transform.position = position;

            if (prefab == null)
            {
                Debug.LogError($"[Rooftop Contract] '{name}' has no prefab assigned in CastingConfig.");
                return root;
            }

            var model = (GameObject)PrefabUtility.InstantiatePrefab(prefab, root.transform);
            model.name = "Visual";
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.Euler(0f, casting.modelYawOffset, 0f);
            model.transform.localScale = Vector3.one;

            NormalizeModel(model, root.transform);
            RC_BuildUtil.SetLayerRecursive(root, GameLayers.Actor);

            var dmg = root.AddComponent<Damageable>();
            dmg.maxHealth = health;

            var setup = root.AddComponent<ActorSetup>();
            setup.headHeightFraction = casting.headHeightFraction;
            setup.headPadding = casting.headPadding;
            setup.bodyPadding = casting.bodyPadding;
            setup.maxBodyWidthFraction = casting.maxBodyWidthFraction;
            setup.headDamageMultiplier = casting.headDamageMultiplier;

            root.AddComponent<ActorDeath>();

            if (addAgent)
            {
                var agent = root.AddComponent<NavMeshAgent>();
                agent.radius = casting.agentRadius;
                agent.height = Mathf.Max(0.6f, casting.targetHeight);
                agent.baseOffset = 0f;
                agent.acceleration = 16f;
                agent.angularSpeed = 620f;
                agent.stoppingDistance = 0.35f;
                agent.obstacleAvoidanceType = ObstacleAvoidanceType.LowQualityObstacleAvoidance;
                agent.autoBraking = true;
            }

            // Animation is optional: a raw MagicaVoxel export has no rig and no clips, and the
            // game reads Speed defensively everywhere, so an actor without an Animator is fine.
            var animator = model.GetComponentInChildren<Animator>();
            if (animator != null)
            {
                var controller = GetAnimatorFor(prefab, role);
                if (controller != null) animator.runtimeAnimatorController = controller;
                animator.applyRootMotion = false;
                // Humanoid IK is part of the Animator evaluation. CullUpdateTransforms skips
                // retargeting and IK whenever Unity decides the renderer is not visible; that is
                // especially unreliable here because guards are tiny at 250 m and assault actors
                // begin outside the camera view. Keep the small cast evaluating so support-hand
                // IK is already correct when a scope or camera turn reveals them.
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }

            return root;
        }

        /// <summary>
        /// MagicaVoxel exports are usually one unit per voxel and pivoted at the model centre.
        /// Scale to a human height and drop the feet onto the root, so 1.8m means 1.8m
        /// everywhere downstream: agent size, hitboxes, weapon offsets, camera height.
        /// </summary>
        static void NormalizeModel(GameObject model, Transform root)
        {
            if (casting.normalizeHeight)
            {
                ActorSetup.NormalizeToHeight(model.transform, casting.targetHeight,
                                             casting.groundPivot, root.position);
            }
            else if (casting.groundPivot && ActorSetup.TryMeasureWorldBounds(model, out Bounds g))
            {
                model.transform.position += new Vector3(
                    root.position.x - g.center.x,
                    root.position.y - g.min.y,
                    root.position.z - g.center.z);
            }
        }

        // ------------------------------------------------------------------ animation

        // ------------------------------------------------------------------ animation

        static readonly Dictionary<string, AnimatorController> controllerCache = new Dictionary<string, AnimatorController>();

        /// <summary>
        /// One controller per character, built from the clips sitting next to that character.
        ///
        /// The Idle state MUST have a real motion. A Humanoid Animator sitting in an empty state
        /// does not hold the bind pose — it drives every muscle to zero and the character folds
        /// into a crouch, which is exactly what the guards were doing.
        /// </summary>
        /// <summary>
        /// Roles exist here to disambiguate shared clips. A folder full of animations named
        /// "Guard1RifleRun" and "VIPRunning" both match "run" — the role decides which one a
        /// given actor should prefer, using the "vip" tag already present in the filenames.
        ///
        /// Guard also means *armed*, which decides the standing pose (see GetAnimatorFor).
        /// Civilian shares the guard's clip tagging but not its weapon-holding stance.
        /// </summary>
        public enum ActorRole { Guard, Vip, Civilian }

        static AnimatorController GetAnimatorFor(GameObject characterPrefab, ActorRole role)
        {
            string folder = FolderOf(characterPrefab);
            if (folder == null) return null;

            string key = $"{folder}|{role}";
            if (controllerCache.TryGetValue(key, out var cached)) return cached;

            AnimationClip plainIdle = ResolveClip("idle", role, folder) ?? casting.idleClip;
            AnimationClip walk = ResolveClip("walk", role, folder) ?? casting.walkClip;
            AnimationClip run = ResolveClip("run", role, folder);
            AnimationClip deathHead = ResolveClip("headshotdeath", role, folder) ?? ResolveClip("death", role, folder);
            AnimationClip deathBody = ResolveClip("bodyshotdeath", role, folder) ?? deathHead;
            AnimationClip terrified = role == ActorRole.Vip ? ResolveClip("terrified", role, folder) : null;

            // Idle variations. These are one-shot flavour states, not part of the locomotion
            // blend — a guard either stands and scans, or checks a boot, never a mix of both.
            // Armed roles only: both clips are authored holding a rifle, so on a VIP or a
            // civilian they would mime a weapon that is not there.
            bool armed = role == ActorRole.Guard;
            AnimationClip idleLook = armed ? ResolveClip("lookaround", role, folder) : null;
            AnimationClip idleSlack = armed ? ResolveClip("checkshoe", role, folder) : null;
            AnimationClip pistolIdle = armed ? ResolveClip("pistolidle", role, folder) : null;
            AnimationClip pistolRun = armed ? ResolveClip("pistolrun", role, folder) : null;
            AnimationClip pistolKneelIdle = armed ? ResolveClip("pistolkneelingidle", role, folder) : null;
            AnimationClip pistolStandToKneel = armed ? ResolveClip("pistolstandtokneel", role, folder) : null;
            AnimationClip pistolKneelToStand = armed ? ResolveClip("pistolkneeltostand", role, folder) : null;
            AnimationClip pistolShooting = armed ? ResolveClip("pistolshooting", role, folder) : null;
            AnimationClip rifleShooting = armed ? ResolveClip("rifleshooting", role, folder) : null;

            // An armed actor's default stance is the weapon-holding scan, not the plain idle.
            //
            // The plain idle is attention: arms straight down at the sides. There is no good
            // place for a rifle in that pose — wherever the weapon goes it hangs off a hip — and
            // no offset fixes it, because the problem is the pose, not the attachment. The
            // lookaround clip holds the weapon up in two hands, which is both what a close
            // protection detail actually does and the only stance a gun sits in convincingly.
            AnimationClip idle = idleLook ?? plainIdle;
            bool lookIsBaseIdle = idleLook != null;

            if (idle == null && walk == null && run == null)
            {
                Debug.LogWarning($"[Rooftop Contract] '{characterPrefab.name}': 애니메이션 클립을 찾지 못했습니다. " +
                                 "Humanoid 리그라면 자세가 무너집니다 — Idle 클립이 최소 하나는 필요합니다.");
                return null;
            }

            string name = System.IO.Path.GetFileName(folder);
            string path = $"Assets/_Game/Animation/RC_{name}.controller";
            AssetDatabase.DeleteAsset(path);

            var controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            controller.AddParameter("Speed", AnimatorControllerParameterType.Float);

            // Without this the engine never calls OnAnimatorIK and WeaponSupportIK is dead code.
            // layers returns a copy of the array, so it has to be written back.
            var layers = controller.layers;
            layers[0].iKPass = true;
            controller.layers = layers;

            // A 1D blend on Speed rather than three states with transitions. Actors here move at
            // wildly different speeds — a guard loiters at 1.1 m/s and sprints to the shield
            // formation at 4.2 — and a blend picks and cross-fades the right clip at every value
            // in between without a threshold to hand-tune.
            var state = controller.CreateBlendTreeInController("Locomotion", out BlendTree tree);
            tree.blendType = BlendTreeType.Simple1D;
            tree.blendParameter = "Speed";
            tree.useAutomaticThresholds = false;

            // Thresholds are the real m/s each clip is authored for, so feet match ground speed.
            if (idle != null) tree.AddChild(idle, 0f);
            if (walk != null) tree.AddChild(walk, 1.4f);
            if (run != null) tree.AddChild(run, 4.2f);

            // A blend tree needs at least one child; fall back to whatever single clip exists.
            if (tree.children.Length == 0)
                tree.AddChild(idle ?? walk ?? run, 0f);

            var sm = controller.layers[0].stateMachine;
            sm.defaultState = state;

            // Death is entered from AnyState so it interrupts whatever the actor was doing.
            // Separate head and body clips: in a sniper game the difference between the two is
            // the feedback that tells the player what kind of hit they just made.
            AddDeathState(controller, sm, "DeathHead", deathHead);
            AddDeathState(controller, sm, "DeathBody", deathBody);

            // No IdleLook trigger when the scan already *is* the loop — triggering a one-shot of
            // the clip the actor is standing in would be an invisible transition to itself.
            AddIdleVariant(controller, sm, state, "IdleLook", lookIsBaseIdle ? null : idleLook);
            AddIdleVariant(controller, sm, state, "IdleSlack", idleSlack);
            AddRifleShooting(controller, sm, state, rifleShooting);
            AddPistolStates(controller, sm, state, pistolIdle, pistolRun, pistolKneelIdle,
                            pistolStandToKneel, pistolKneelToStand, pistolShooting);
            AddTerrified(controller, sm, state, terrified);

            AssetDatabase.SaveAssets();
            controllerCache[key] = controller;

            Debug.Log($"[Rooftop Contract] '{name}' ({role}) 애니메이터 — " +
                      $"idle: {ClipName(idle)}, walk: {ClipName(walk)}, run: {ClipName(run)}, " +
                      $"death: {ClipName(deathHead)} / {ClipName(deathBody)}");
            return controller;
        }

        static void AddRifleShooting(AnimatorController controller, AnimatorStateMachine sm,
                                     AnimatorState locomotion, AnimationClip clip)
        {
            if (clip == null) return;
            controller.AddParameter("RifleFire", AnimatorControllerParameterType.Trigger);
            var firing = sm.AddState("RifleShooting");
            firing.motion = clip;

            var enter = locomotion.AddTransition(firing);
            enter.AddCondition(AnimatorConditionMode.If, 0f, "RifleFire");
            enter.duration = 0.04f;
            enter.hasExitTime = false;

            var recover = firing.AddTransition(locomotion);
            recover.hasExitTime = true;
            recover.exitTime = 0.92f;
            recover.duration = 0.08f;
        }

        static void AddTerrified(AnimatorController controller, AnimatorStateMachine sm,
                                 AnimatorState locomotion, AnimationClip clip)
        {
            if (clip == null) return;
            controller.AddParameter("Terrified", AnimatorControllerParameterType.Trigger);
            var terrified = sm.AddState("Terrified");
            terrified.motion = clip;

            var enter = locomotion.AddTransition(terrified);
            enter.AddCondition(AnimatorConditionMode.If, 0f, "Terrified");
            enter.duration = 0.08f;
            enter.hasExitTime = false;

            var recover = terrified.AddTransition(locomotion);
            recover.hasExitTime = true;
            recover.exitTime = 0.95f;
            recover.duration = 0.1f;
        }

        static void AddPistolStates(AnimatorController controller, AnimatorStateMachine sm,
                                    AnimatorState rifleLocomotion, AnimationClip idle,
                                    AnimationClip run, AnimationClip kneelIdle,
                                    AnimationClip standToKneel, AnimationClip kneelToStand,
                                    AnimationClip shooting)
        {
            // Do not create a half-working pistol graph. The three stable poses are the minimum
            // needed to guarantee standing movement and kneeling cover on every Humanoid guard.
            if (idle == null || run == null || kneelIdle == null) return;

            controller.AddParameter("IsPistol", AnimatorControllerParameterType.Bool);
            controller.AddParameter("PistolKneel", AnimatorControllerParameterType.Bool);
            controller.AddParameter("PistolFire", AnimatorControllerParameterType.Trigger);

            var standing = controller.CreateBlendTreeInController("PistolStanding", out BlendTree tree);
            tree.blendType = BlendTreeType.Simple1D;
            tree.blendParameter = "Speed";
            tree.useAutomaticThresholds = false;
            tree.AddChild(idle, 0f);
            tree.AddChild(run, 4.2f);

            var toPistol = rifleLocomotion.AddTransition(standing);
            toPistol.AddCondition(AnimatorConditionMode.If, 0f, "IsPistol");
            toPistol.duration = 0.12f;
            toPistol.hasExitTime = false;

            var toRifle = standing.AddTransition(rifleLocomotion);
            toRifle.AddCondition(AnimatorConditionMode.IfNot, 0f, "IsPistol");
            toRifle.duration = 0.12f;
            toRifle.hasExitTime = false;

            var kneeling = sm.AddState("PistolKneeling");
            kneeling.motion = kneelIdle;

            AnimatorState kneelEntry = kneeling;
            if (standToKneel != null)
            {
                kneelEntry = sm.AddState("PistolStandToKneel");
                kneelEntry.motion = standToKneel;
                var finish = kneelEntry.AddTransition(kneeling);
                finish.hasExitTime = true;
                finish.exitTime = 0.95f;
                finish.duration = 0.08f;
            }

            var kneel = standing.AddTransition(kneelEntry);
            kneel.AddCondition(AnimatorConditionMode.If, 0f, "PistolKneel");
            kneel.duration = 0.1f;
            kneel.hasExitTime = false;

            AnimatorState standingExit = standing;
            if (kneelToStand != null)
            {
                standingExit = sm.AddState("PistolKneelToStand");
                standingExit.motion = kneelToStand;
                var finish = standingExit.AddTransition(standing);
                finish.hasExitTime = true;
                finish.exitTime = 0.95f;
                finish.duration = 0.08f;
            }

            var stand = kneeling.AddTransition(standingExit);
            stand.AddCondition(AnimatorConditionMode.IfNot, 0f, "PistolKneel");
            stand.duration = 0.1f;
            stand.hasExitTime = false;

            if (shooting != null)
            {
                var fire = sm.AddState("PistolShooting");
                fire.motion = shooting;
                var shoot = kneeling.AddTransition(fire);
                shoot.AddCondition(AnimatorConditionMode.If, 0f, "PistolFire");
                shoot.duration = 0.04f;
                shoot.hasExitTime = false;

                var recover = fire.AddTransition(kneeling);
                recover.hasExitTime = true;
                recover.exitTime = 0.92f;
                recover.duration = 0.06f;

                var leaveCover = fire.AddTransition(standingExit);
                leaveCover.AddCondition(AnimatorConditionMode.IfNot, 0f, "PistolKneel");
                leaveCover.duration = 0.08f;
                leaveCover.hasExitTime = false;
            }
        }

        /// <summary>
        /// A one-shot idle flourish that returns to locomotion by itself, and bails out early the
        /// moment the actor starts moving — a guard caught mid-scan should walk, not finish the
        /// pose first.
        /// </summary>
        static void AddIdleVariant(AnimatorController controller, AnimatorStateMachine sm,
                                   AnimatorState locomotion, string triggerName, AnimationClip clip)
        {
            if (clip == null) return;

            controller.AddParameter(triggerName, AnimatorControllerParameterType.Trigger);

            var state = sm.AddState(triggerName);
            state.motion = clip;

            var enter = locomotion.AddTransition(state);
            enter.AddCondition(AnimatorConditionMode.If, 0f, triggerName);
            enter.AddCondition(AnimatorConditionMode.Less, 0.12f, "Speed");
            enter.duration = 0.25f;
            enter.hasExitTime = false;

            var finish = state.AddTransition(locomotion);
            finish.hasExitTime = true;
            finish.exitTime = 0.95f;
            finish.duration = 0.25f;

            var interrupt = state.AddTransition(locomotion);
            interrupt.AddCondition(AnimatorConditionMode.Greater, 0.15f, "Speed");
            interrupt.duration = 0.12f;
            interrupt.hasExitTime = false;
        }

        static void AddDeathState(AnimatorController controller, AnimatorStateMachine sm,
                                  string triggerName, AnimationClip clip)
        {
            if (clip == null) return;

            controller.AddParameter(triggerName, AnimatorControllerParameterType.Trigger);

            var state = sm.AddState(triggerName);
            state.motion = clip;
            // Nothing leaves a death state. The body holds the last frame until it despawns.
            state.writeDefaultValues = false;

            var t = sm.AddAnyStateTransition(state);
            t.AddCondition(AnimatorConditionMode.If, 0f, triggerName);
            t.duration = 0.08f;
            t.hasExitTime = false;
            t.canTransitionToSelf = false;
        }

        /// <summary>
        /// Clip lookup, most specific first: this character's own folder, then the shared folder,
        /// then any other cast folder — and at each step a role-tagged match beats an untagged
        /// one, so a VIP takes VIPRunning while a guard takes Guard1RifleRun from the same folder.
        /// </summary>
        static AnimationClip ResolveClip(string keyword, ActorRole role, string ownFolder)
        {
            string[] folders = { ownFolder, SharedAnimFolder };

            foreach (var f in folders)
            {
                var tagged = FindClipInFolder(f, keyword, role, requireRoleTag: true);
                if (tagged != null) return tagged;
            }
            foreach (var f in folders)
            {
                var any = FindClipInFolder(f, keyword, role, requireRoleTag: false);
                if (any != null) return any;
            }

            foreach (bool requireTag in new[] { true, false })
            {
                foreach (var pool in new[] { casting.protectorPrefabs, casting.assaultPrefabs,
                                             casting.vipPrefabs, casting.civilianPrefabs })
                {
                    if (pool == null) continue;
                    foreach (var p in pool)
                    {
                        var clip = FindClipInFolder(FolderOf(p), keyword, role, requireTag);
                        if (clip != null) return clip;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// The clip an armed actor stands in — the stance a weapon actually has to fit into.
        /// The grip workbench poses its preview with this so what you place it against is what
        /// the game will show.
        /// </summary>
        public static AnimationClip ArmedIdleClipFor(GameObject characterPrefab)
        {
            return GuardClipFor(characterPrefab, "lookaround") ?? GuardClipFor(characterPrefab, "idle");
        }

        /// <summary>
        /// Any clip an armed actor uses, by the same keyword rules the generator itself follows.
        /// The workbench cycles through these so a grip can be checked against every stance the
        /// weapon will actually be carried in, not just the one it was fitted to.
        /// </summary>
        public static AnimationClip GuardClipFor(GameObject characterPrefab, string keyword)
        {
            if (casting == null) casting = LoadOrCreateCasting();
            string folder = FolderOf(characterPrefab);
            if (folder == null) return null;
            return ResolveClip(keyword, ActorRole.Guard, folder);
        }

        static string ClipName(AnimationClip c) => c != null ? c.name : "없음";

        static string FolderOf(GameObject prefab)
        {
            if (prefab == null) return null;
            string path = AssetDatabase.GetAssetPath(prefab);
            return string.IsNullOrEmpty(path) ? null : System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
        }

        /// <summary>
        /// Matches on the FILE name, not the clip name. Mixamo names the clip after the character
        /// ("Guard2", "Target3") while the file keeps the animation's name ("Guard2Idle.fbx") —
        /// so the filename is the reliable signal for what the animation actually is.
        /// </summary>
        static AnimationClip FindClipInFolder(string folder, string keyword, ActorRole role, bool requireRoleTag)
        {
            if (string.IsNullOrEmpty(folder) || !AssetDatabase.IsValidFolder(folder)) return null;

            var matches = new List<string>();
            foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { folder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                string file = System.IO.Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
                string normalizedFile = NormalizeClipKey(file);
                string normalizedKeyword = NormalizeClipKey(keyword);
                if (!normalizedFile.Contains(normalizedKeyword)) continue;

                bool isVipClip = file.Contains("vip");
                if (requireRoleTag)
                {
                    // A VIP wants the vip-tagged clip; anyone else explicitly does not want it.
                    if (role == ActorRole.Vip ? !isVipClip : isVipClip) continue;
                }
                matches.Add(path);
            }

            // Shortest filename first, ties broken alphabetically.
            //
            // Deterministic matters — FindAssets does not guarantee order — but so does picking
            // the *plainest* match. "Guard1Idle", "Guard1CheckShoeIdle" and
            // "Guard1HoldRifleLookaroundIdle" all contain "idle"; alphabetical order hands back
            // the shoe-check loiter as every guard's default stance. The undecorated name is the
            // shortest one, and that is the one that means "just idle".
            matches.Sort((a, b) =>
            {
                string fa = System.IO.Path.GetFileNameWithoutExtension(a);
                string fb = System.IO.Path.GetFileNameWithoutExtension(b);
                int byLength = fa.Length.CompareTo(fb.Length);
                return byLength != 0 ? byLength : string.CompareOrdinal(fa, fb);
            });

            foreach (var path in matches)
            {
                var clip = FirstClipAt(path);
                if (clip != null) return clip;
            }
            return null;
        }

        static string NormalizeClipKey(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var chars = new char[value.Length];
            int count = 0;
            foreach (char c in value)
                if (char.IsLetterOrDigit(c)) chars[count++] = char.ToLowerInvariant(c);
            return new string(chars, 0, count);
        }

        /// <summary>
        /// Shared clips live here. Every character is Humanoid, so one Walk retargets onto all of
        /// them — this is the folder to drop a clip in when you want it used by everyone.
        /// </summary>
        const string SharedAnimFolder = "Assets/VoxelCharacter/_Animations";


        static AnimationClip FirstClipAt(string assetPath)
        {
            foreach (var obj in AssetDatabase.LoadAllAssetsAtPath(assetPath))
            {
                if (obj is not AnimationClip clip) continue;
                if (clip.name.StartsWith("__preview__")) continue;
                return clip;
            }
            return null;
        }

        // ------------------------------------------------------------------ navmesh

        static void BuildNavMesh(string scenePath, RC_MapBuilder.MapRefs map)
        {
            // Every rebuild bakes fresh data; clear the previous run's assets so they do not
            // pile up as "NavMesh-NavMesh_Nest 1.asset", "… 2.asset" and so on.
            string folder = NavMeshFolder(scenePath);
            foreach (var guid in AssetDatabase.FindAssets("t:NavMeshData", new[] { folder }))
                AssetDatabase.DeleteAsset(AssetDatabase.GUIDToAssetPath(guid));

            // Colliders created this same frame are not in the physics scene yet, which is what
            // made a Volume + PhysicsColliders bake collect nothing at all.
            Physics.SyncTransforms();

            // One surface per rooftop, parented to that rooftop and collecting its children.
            // "Children" needs no volume arithmetic and no physics query — and because each
            // roof is its own hierarchy, the two surfaces still cannot link across the 250m gap.
            // Probe the target's own spawn rather than the venue origin. On a terrace those are
            // the same point; on the street venue the origin is out in the middle of the road
            // and the walkable surface is the raised pavement beside it.
            BakeSurface(scenePath, "NavMesh_Terrace", map.terrace, map.targetSpawn);
            BakeSurface(scenePath, "NavMesh_Nest", map.nest, RC_MapBuilder.NestCenter);
        }

        static void BakeSurface(string scenePath, string name, Transform rooftop, Vector3 probe)
        {
            if (rooftop == null)
            {
                Debug.LogError($"[Rooftop Contract] {name}: rooftop transform missing.");
                return;
            }

            // The surface GameObject stays at the world origin, unparented.
            //
            // NavMeshSurface.AddData() instantiates the data at transform.position
            // (NavMeshSurface.cs:221), while the sources below are collected as world-space
            // matrices and built with an identity frame. Parenting this to the rooftop offset
            // the whole NavMesh by the rooftop's height — a terrace at y=34 baked a NavMesh
            // at y=68, which reads exactly like "nothing was collected".
            var go = new GameObject(name);
            go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

            var surface = go.AddComponent<NavMeshSurface>();
            surface.collectObjects = CollectObjects.Children;
            surface.useGeometry = NavMeshCollectGeometry.RenderMeshes;

            // Collect the geometry by hand instead of letting NavMeshSurface do it.
            //
            // Its editor path routes through NavMeshBuilder.CollectSourcesInStage, which depends
            // on the scene being the active stage — and in a generator that creates a scene,
            // fills it, saves it and bakes all in one call, that returned nothing every time
            // (both Volume+PhysicsColliders and Children+RenderMeshes). Walking the rooftop's
            // own MeshFilters is unambiguous, and it lets the failure be reported with a count
            // rather than an empty bounds.
            var sources = new List<NavMeshBuildSource>();
            Bounds worldBounds = default;
            bool first = true;

            foreach (var mf in rooftop.GetComponentsInChildren<MeshFilter>(false))
            {
                var mesh = mf.sharedMesh;
                if (mesh == null) continue;
                var renderer = mf.GetComponent<MeshRenderer>();
                if (renderer == null || !renderer.enabled) continue;

                sources.Add(new NavMeshBuildSource
                {
                    shape = NavMeshBuildSourceShape.Mesh,
                    sourceObject = mesh,
                    transform = mf.transform.localToWorldMatrix,
                    area = 0,
                });

                if (first) { worldBounds = renderer.bounds; first = false; }
                else worldBounds.Encapsulate(renderer.bounds);
            }

            if (sources.Count == 0)
            {
                Debug.LogError($"[Rooftop Contract] {name}: '{rooftop.name}' 아래에서 메시를 하나도 찾지 못했습니다 " +
                               $"(자식 {rooftop.childCount}개). 맵 빌더가 옥상을 만들지 못한 것입니다.");
                return;
            }

            // A little headroom so the walkable surface is not clipped by its own bounding box.
            worldBounds.Expand(2f);

            var settings = NavMesh.GetSettingsByID(0);
            var data = UnityEngine.AI.NavMeshBuilder.BuildNavMeshData(
                settings, sources, worldBounds, Vector3.zero, Quaternion.identity);

            surface.navMeshData = data;
            if (data != null) surface.AddData();

            // BuildNavMesh() only produces NavMeshData in memory. The editor UI follows it with
            // an AssetDatabase.CreateAsset; called from script that step is ours to do. Skip it
            // and the reference serialises as null, every agent lands off-mesh, and the entire
            // AI silently stands still — which is exactly what an unbaked build looks like.
            if (surface.navMeshData == null)
            {
                Debug.LogError($"[Rooftop Contract] {name}: bake produced no data. " +
                               "Check that the rooftop colliders fall inside the volume.");
                return;
            }

            // Sample BEFORE writing the asset. AssetDatabase.CreateAsset re-homes the object it
            // is given, which detaches the instance BuildNavMesh registered with the live NavMesh
            // — so a check placed after it reports "not walkable" on a perfectly good bake.
            bool walkableLive = NavMesh.SamplePosition(probe, out NavMeshHit sample, 4f, NavMesh.AllAreas);
            Bounds src = surface.navMeshData.sourceBounds;

            AssetDatabase.CreateAsset(surface.navMeshData,
                AssetDatabase.GenerateUniqueAssetPath($"{NavMeshFolder(scenePath)}/NavMesh-{name}.asset"));
            AssetDatabase.SaveAssets();

            // Re-register the (now asset-backed) data so the scene we are about to save holds a
            // surface whose data is both persisted and live.
            surface.RemoveData();
            surface.AddData();
            bool walkableAfter = NavMesh.SamplePosition(probe, out _, 4f, NavMesh.AllAreas);

            // Hard facts, not inference: how many triangles the navigation system actually holds,
            // and where they are. If this ever fails again these two numbers say why immediately.
            var tri = NavMesh.CalculateTriangulation();
            Bounds triBounds = default;
            if (tri.vertices != null && tri.vertices.Length > 0)
            {
                triBounds = new Bounds(tri.vertices[0], Vector3.zero);
                foreach (var v in tri.vertices) triBounds.Encapsulate(v);
            }

            if (walkableLive || walkableAfter)
            {
                Debug.Log($"[Rooftop Contract] {name} baked — walkable: YES " +
                          $"(메시 {sources.Count}개 → 삼각형 {tri.indices.Length / 3}개, " +
                          $"probe {probe} → hit {sample.position})");
            }
            else
            {
                Debug.LogError($"[Rooftop Contract] {name} baked but NOT walkable at {probe}.\n" +
                               $"  수집한 메시 : {sources.Count}개, 범위 {worldBounds.size}\n" +
                               $"  navmesh 삼각형: {tri.indices.Length / 3}개\n" +
                               $"  navmesh 실제 위치: centre {triBounds.center}, size {triBounds.size}\n" +
                               $"  ↑ centre 의 y 가 probe 의 y 와 다르면 좌표계가 어긋난 것입니다.");
            }
        }

        /// <summary>Mirrors the package's own layout: a folder named after the scene, beside it.</summary>
        static string NavMeshFolder(string scenePath)
        {
            string parent = Path.GetDirectoryName(scenePath).Replace('\\', '/');
            string leaf = Path.GetFileNameWithoutExtension(scenePath);
            string folder = $"{parent}/{leaf}";
            if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder(parent, leaf);
            return folder;
        }

        // ------------------------------------------------------------------ hud

        static void BuildHud(PlayerRoot player, VipTarget target)
        {
            var go = new GameObject("HUD");
            var hud = go.AddComponent<HudController>();
            hud.player = player;
            // The HUD reads the walk's remaining time straight off the target, so the countdown
            // is the actual route rather than a number someone typed next to it.
            hud.target = target;
            if (player != null)
            {
                hud.sniper = player.sniper;
                hud.pistol = player.pistol;
            }

            go.AddComponent<SubtitleFeed>();
        }
    }
}
