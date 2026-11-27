using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Tl.Unity.Editor
{
    [InitializeOnLoad]
#if UNITY_7000_0_OR_NEWER
    [global::Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
#endif
    public static class TlFrameRecorder
    {
        public sealed class Settings
        {
            public const int DefaultFrames = 150;
            public const int DefaultEvery = 2;
            public const int DefaultWidth = 1280;
            public const int DefaultHeight = 720;
            public const float DefaultTimeoutSeconds = 150f;

            public string ScenePath;
            public int Frames = DefaultFrames;
            public int Every = DefaultEvery;
            public int Width = DefaultWidth;
            public int Height = DefaultHeight;
            public string OutputFolder;
            public Camera Camera;
            public float TimeoutSeconds = DefaultTimeoutSeconds;
        }

        [Serializable]
        public sealed class Result
        {
            public bool Succeeded;
            public string Error;
            public string OutputFolder;
            public int FramesRecorded;
            public int RowsWritten;
            public int PicturesWritten;
            public bool GraphicsAvailable;
        }

        [Serializable]
        sealed class PendingRequest
        {
            public string ScenePath;
            public int Frames;
            public int Every;
            public int Width;
            public int Height;
            public string OutputFolder;
            public string CameraPath;
            public float TimeoutSeconds;
            public bool ExitEditor;
        }

        const string PendingKey = "Tl.Unity.FrameRecorder.Pending";
        const string ActiveKey = "Tl.Unity.FrameRecorder.Active";
        const string ExitKey = "Tl.Unity.FrameRecorder.ExitCode";
        const string ExitOnAbortKey = "Tl.Unity.FrameRecorder.ExitOnAbort";
        const string ResultKey = "Tl.Unity.FrameRecorder.LastResult";

        static bool s_Active;
        static bool s_CaptureRateActive;
        static int s_PendingExitCode = -1;
        static Camera s_LiveCamera;
        static GameObject s_HostObject;
        static Result s_LastResult;

        static TlFrameRecorder()
        {
            if (AssetDatabase.IsAssetImportWorkerProcess())
                return;
            AssemblyReloadEvents.beforeAssemblyReload += Release;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        public static bool IsRecording =>
            s_Active ||
            SessionState.GetBool(ActiveKey, false) ||
            !string.IsNullOrEmpty(SessionState.GetString(PendingKey, string.Empty));

        public static Result LastResult
        {
            get
            {
                if (s_LastResult != null)
                    return s_LastResult;
                var json = SessionState.GetString(ResultKey, string.Empty);
                return string.IsNullOrEmpty(json) ? null : JsonUtility.FromJson<Result>(json);
            }
        }

        [MenuItem("Tools/Tl/Record Frames")]
        static void RecordOpenSceneFromMenu()
        {
            Start(new Settings());
        }

        [MenuItem("Tools/Tl/Record Frames", true)]
        static bool CanRecordFromMenu() => !IsRecording;

        public static void Start(Settings settings = null)
        {
            if (IsRecording)
                throw new InvalidOperationException("TlFrameRecorder is already recording.");

            var request = BuildRequest(settings ?? new Settings(), exitEditor: false, out var sceneToOpen, out var liveCamera);
            ClearSessionMarkers();
            s_LiveCamera = liveCamera;

            if (EditorApplication.isPlaying)
            {
                if (!string.IsNullOrEmpty(sceneToOpen) && !string.Equals(SceneManager.GetActiveScene().path, sceneToOpen, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Cannot open a different scene while Play mode is already running: " + sceneToOpen);
                BeginInPlayMode(request, liveCamera, exitPlayModeWhenDone: true);
                return;
            }

            if (!string.IsNullOrEmpty(sceneToOpen) && !string.Equals(SceneManager.GetActiveScene().path, sceneToOpen, StringComparison.OrdinalIgnoreCase))
                EditorSceneManager.OpenScene(sceneToOpen, OpenSceneMode.Single);

            SessionState.SetString(PendingKey, JsonUtility.ToJson(request));
            EditorApplication.isPlaying = true;
        }

        public static IEnumerator RecordInPlayMode(Settings settings, Action<Result> onComplete = null)
        {
            if (!EditorApplication.isPlaying)
                throw new InvalidOperationException("RecordInPlayMode must be called while Play mode is active.");
            if (IsRecording)
                throw new InvalidOperationException("TlFrameRecorder is already recording.");

            var options = settings ?? new Settings();
            ValidateSettings(options);
            var activeScene = SceneManager.GetActiveScene();
            var sceneName = string.IsNullOrEmpty(activeScene.name) ? "Scene" : activeScene.name;
            var request = new PendingRequest
            {
                ScenePath = activeScene.path,
                Frames = options.Frames,
                Every = options.Every,
                Width = options.Width,
                Height = options.Height,
                OutputFolder = ResolveOutputFolder(options.OutputFolder, sceneName),
                CameraPath = options.Camera != null ? HierarchyPath(options.Camera.transform) : string.Empty,
                TimeoutSeconds = options.TimeoutSeconds,
                ExitEditor = false
            };

            ClearSessionMarkers();
            s_Active = true;
            SessionState.SetBool(ActiveKey, true);
            DestroyHostObject();
            var hostObject = new GameObject("TlFrameRecorderHost")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            UnityEngine.Object.DontDestroyOnLoad(hostObject);
            s_HostObject = hostObject;
            var host = hostObject.AddComponent<TlFrameRecorderHost>();
            host.OnDestroyed = ResetCaptureFramerate;

            Result result = null;
            var done = false;
            try
            {
                host.StartCoroutine(CaptureRoutine(request, options.Camera, r =>
                {
                    result = r;
                    done = true;
                }));
                while (!done && host != null && EditorApplication.isPlaying)
                    yield return null;
            }
            finally
            {
                s_Active = false;
                SessionState.EraseBool(ActiveKey);
                ResetCaptureFramerate();
                if (host != null && host.gameObject != null)
                {
                    if (s_HostObject == host.gameObject)
                        s_HostObject = null;
                    UnityEngine.Object.Destroy(host.gameObject);
                }
                else
                {
                    DestroyHostObject();
                }
            }

            StoreResult(result);
            onComplete?.Invoke(result);
        }

        public static void RecordFromCommandLine()
        {
            if (!TryParseCommandLine(Environment.GetCommandLineArgs(), out var settings, out var error))
            {
                Debug.LogError("TlFrameRecorder: " + error);
                EditorApplication.Exit(1);
                return;
            }

            PendingRequest request;
            try
            {
                var normalizedScene = NormalizeScenePath(settings.ScenePath);
                settings.ScenePath = normalizedScene;
                if (!SceneExists(normalizedScene))
                {
                    Debug.LogError("TlFrameRecorder: scene not found: " + normalizedScene);
                    EditorApplication.Exit(1);
                    return;
                }
                if (!string.Equals(SceneManager.GetActiveScene().path, normalizedScene, StringComparison.OrdinalIgnoreCase))
                    EditorSceneManager.OpenScene(normalizedScene, OpenSceneMode.Single);
                request = BuildRequest(settings, exitEditor: true, out _, out _);
            }
            catch (Exception exception)
            {
                Debug.LogError("TlFrameRecorder: failed to open scene '" + settings.ScenePath + "': " + exception.Message);
                EditorApplication.Exit(1);
                return;
            }

            ClearSessionMarkers();
            s_LiveCamera = null;
            SessionState.SetString(PendingKey, JsonUtility.ToJson(request));
            EditorApplication.isPlaying = true;
        }

        public static bool TryParseCommandLine(string[] args, out Settings settings, out string error)
        {
            settings = null;
            error = null;
            if (args == null)
            {
                error = "No command line arguments provided.";
                return false;
            }

            var parsed = new Settings
            {
                Frames = 0
            };
            var hasScene = false;
            var hasFrames = false;

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (string.IsNullOrEmpty(arg))
                    continue;

                if (arg == "-tlRecordScene")
                {
                    if (!TryReadArgValue(args, ref i, arg, out var value, out error))
                        return false;
                    parsed.ScenePath = value;
                    hasScene = true;
                }
                else if (arg == "-tlRecordFrames")
                {
                    if (!TryReadPositiveIntArg(args, ref i, arg, out var frames, out error))
                        return false;
                    parsed.Frames = frames;
                    hasFrames = true;
                }
                else if (arg == "-tlRecordEvery")
                {
                    if (!TryReadPositiveIntArg(args, ref i, arg, out var every, out error))
                        return false;
                    parsed.Every = every;
                }
                else if (arg == "-tlRecordOut")
                {
                    if (!TryReadArgValue(args, ref i, arg, out var output, out error))
                        return false;
                    parsed.OutputFolder = output;
                }
                else if (arg == "-tlRecordWidth")
                {
                    if (!TryReadPositiveIntArg(args, ref i, arg, out var width, out error))
                        return false;
                    parsed.Width = width;
                }
                else if (arg == "-tlRecordHeight")
                {
                    if (!TryReadPositiveIntArg(args, ref i, arg, out var height, out error))
                        return false;
                    parsed.Height = height;
                }
                else if (arg.StartsWith("-tlRecord", StringComparison.OrdinalIgnoreCase))
                {
                    error = "Unknown argument: " + arg + ".";
                    return false;
                }
            }

            if (!hasScene || string.IsNullOrWhiteSpace(parsed.ScenePath))
            {
                error = "Missing required argument -tlRecordScene <path>.";
                return false;
            }

            if (!hasFrames || parsed.Frames <= 0)
            {
                error = "Missing required argument -tlRecordFrames <count>.";
                return false;
            }

            settings = parsed;
            return true;
        }

        static bool TryReadArgValue(string[] args, ref int index, string flag, out string value, out string error)
        {
            if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]) || args[index + 1].StartsWith("-", StringComparison.Ordinal))
            {
                value = null;
                error = "Missing value for " + flag + ".";
                return false;
            }
            index++;
            value = args[index];
            error = null;
            return true;
        }

        static bool TryReadPositiveIntArg(string[] args, ref int index, string flag, out int value, out string error)
        {
            value = 0;
            if (!TryReadArgValue(args, ref index, flag, out var text, out error))
                return false;
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value <= 0)
            {
                error = "Invalid value for " + flag + ": '" + text + "' (expected a positive integer).";
                return false;
            }
            return true;
        }

        static void Release()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= Release;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.update -= ExitAfterEditMode;
        }

        static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.EnteredPlayMode)
            {
                var json = SessionState.GetString(PendingKey, string.Empty);
                if (string.IsNullOrEmpty(json))
                    return;
                SessionState.EraseString(PendingKey);
                var request = JsonUtility.FromJson<PendingRequest>(json);
                var liveCamera = s_LiveCamera;
                s_LiveCamera = null;
                BeginInPlayMode(request, liveCamera, exitPlayModeWhenDone: true);
            }
            else if (change == PlayModeStateChange.ExitingPlayMode)
            {
                ResetCaptureFramerate();
                DestroyHostObject();
                s_LiveCamera = null;
                if (SessionState.GetBool(ActiveKey, false))
                {
                    s_Active = false;
                    SessionState.EraseBool(ActiveKey);
                    var aborted = new Result
                    {
                        Succeeded = false,
                        Error = "Recording aborted because Play mode exited early."
                    };
                    StoreResult(aborted);
                    Debug.LogWarning("TlFrameRecorder: " + aborted.Error);
                    if (SessionState.GetBool(ExitOnAbortKey, false))
                    {
                        SessionState.EraseBool(ExitOnAbortKey);
                        SessionState.SetInt(ExitKey, 1);
                    }
                }
            }
            else if (change == PlayModeStateChange.EnteredEditMode)
            {
                ResetCaptureFramerate();
                DestroyHostObject();
                s_Active = false;
                SessionState.EraseBool(ActiveKey);
                var exitCode = SessionState.GetInt(ExitKey, -1);
                if (exitCode >= 0)
                {
                    SessionState.EraseInt(ExitKey);
                    s_PendingExitCode = exitCode;
                    EditorApplication.update += ExitAfterEditMode;
                }
            }
        }

        static void ExitAfterEditMode()
        {
            EditorApplication.update -= ExitAfterEditMode;
            var code = s_PendingExitCode;
            s_PendingExitCode = -1;
            EditorApplication.Exit(code < 0 ? 1 : code);
        }

        static PendingRequest BuildRequest(Settings settings, bool exitEditor, out string sceneToOpen, out Camera liveCamera)
        {
            ValidateSettings(settings);
            var activeScene = SceneManager.GetActiveScene();
            sceneToOpen = string.IsNullOrWhiteSpace(settings.ScenePath) ? activeScene.path : NormalizeScenePath(settings.ScenePath);
            if (!string.IsNullOrWhiteSpace(settings.ScenePath) && !SceneExists(sceneToOpen))
                throw new FileNotFoundException("Scene not found: " + sceneToOpen, sceneToOpen);

            var sceneName = !string.IsNullOrEmpty(sceneToOpen)
                ? Path.GetFileNameWithoutExtension(sceneToOpen)
                : !string.IsNullOrEmpty(activeScene.name) ? activeScene.name : "Scene";
            liveCamera = settings.Camera;
            var cameraPath = liveCamera != null ? HierarchyPath(liveCamera.transform) : string.Empty;

            return new PendingRequest
            {
                ScenePath = sceneToOpen,
                Frames = settings.Frames,
                Every = settings.Every,
                Width = settings.Width,
                Height = settings.Height,
                OutputFolder = ResolveOutputFolder(settings.OutputFolder, sceneName),
                CameraPath = cameraPath,
                TimeoutSeconds = settings.TimeoutSeconds,
                ExitEditor = exitEditor
            };
        }

        static void ValidateSettings(Settings settings)
        {
            if (settings.Frames <= 0)
                throw new ArgumentOutOfRangeException(nameof(settings.Frames), "Frames must be greater than zero.");
            if (settings.Every <= 0)
                throw new ArgumentOutOfRangeException(nameof(settings.Every), "Every must be greater than zero.");
            if (settings.Width <= 0)
                throw new ArgumentOutOfRangeException(nameof(settings.Width), "Width must be greater than zero.");
            if (settings.Height <= 0)
                throw new ArgumentOutOfRangeException(nameof(settings.Height), "Height must be greater than zero.");
            if (settings.TimeoutSeconds <= 0f)
                throw new ArgumentOutOfRangeException(nameof(settings.TimeoutSeconds), "TimeoutSeconds must be greater than zero.");
        }

        static string ResolveOutputFolder(string configured, string sceneName)
        {
            var raw = string.IsNullOrWhiteSpace(configured) ? Path.Combine("Logs", "TlFrames", sceneName) : configured;
            return Path.GetFullPath(raw);
        }

        static string NormalizeScenePath(string scenePath)
        {
            if (string.IsNullOrWhiteSpace(scenePath))
                return string.Empty;
            var normalized = scenePath.Trim().Replace('\\', '/');
            var assetsRoot = Application.dataPath.Replace('\\', '/');
            if (normalized.StartsWith(assetsRoot + "/", StringComparison.OrdinalIgnoreCase))
                normalized = "Assets" + normalized.Substring(assetsRoot.Length);
            return normalized;
        }

        static bool SceneExists(string scenePath)
        {
            var normalized = NormalizeScenePath(scenePath);
            if (string.IsNullOrEmpty(normalized))
                return false;
            return File.Exists(normalized) || AssetDatabase.LoadAssetAtPath<SceneAsset>(normalized) != null;
        }

        static string HierarchyPath(Transform transform)
        {
            var path = transform.name;
            var current = transform.parent;
            while (current != null)
            {
                path = current.name + "/" + path;
                current = current.parent;
            }
            return "/" + path;
        }

        static Camera ResolveCamera(Camera liveCamera, string cameraPath)
        {
            if (liveCamera != null)
                return liveCamera;
            if (!string.IsNullOrEmpty(cameraPath))
            {
                var found = GameObject.Find(cameraPath);
                if (found != null)
                {
                    var camera = found.GetComponent<Camera>();
                    if (camera != null)
                        return camera;
                }
            }
            return Camera.main;
        }

        static void ClearSessionMarkers()
        {
            SessionState.EraseString(PendingKey);
            SessionState.EraseBool(ActiveKey);
            SessionState.EraseBool(ExitOnAbortKey);
            SessionState.EraseInt(ExitKey);
            SessionState.EraseString(ResultKey);
            s_LastResult = null;
        }

        static void StoreResult(Result result)
        {
            s_LastResult = result;
            if (result != null)
                SessionState.SetString(ResultKey, JsonUtility.ToJson(result));
        }

        static void ResetCaptureFramerate()
        {
            if (!s_CaptureRateActive && Time.captureFramerate == 0)
                return;
            Time.captureFramerate = 0;
            s_CaptureRateActive = false;
        }

        static void DestroyHostObject()
        {
            if (s_HostObject == null)
                return;
            UnityEngine.Object.DestroyImmediate(s_HostObject);
            s_HostObject = null;
        }

        static void BeginInPlayMode(PendingRequest request, Camera liveCamera, bool exitPlayModeWhenDone)
        {
            s_Active = true;
            SessionState.SetBool(ActiveKey, true);
            if (request.ExitEditor)
                SessionState.SetBool(ExitOnAbortKey, true);
            else
                SessionState.EraseBool(ExitOnAbortKey);

            DestroyHostObject();
            var hostObject = new GameObject("TlFrameRecorderHost")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            UnityEngine.Object.DontDestroyOnLoad(hostObject);
            s_HostObject = hostObject;
            var host = hostObject.AddComponent<TlFrameRecorderHost>();
            host.OnDestroyed = ResetCaptureFramerate;
            host.StartCoroutine(RunHosted(host, request, liveCamera, exitPlayModeWhenDone));
        }

        static IEnumerator RunHosted(TlFrameRecorderHost host, PendingRequest request, Camera liveCamera, bool exitPlayModeWhenDone)
        {
            Result result = null;
            try
            {
                yield return CaptureRoutine(request, liveCamera, r => result = r);
            }
            finally
            {
                s_Active = false;
                SessionState.EraseBool(ActiveKey);
                SessionState.EraseBool(ExitOnAbortKey);
                ResetCaptureFramerate();
                StoreResult(result);
                if (host != null && host.gameObject != null)
                {
                    if (s_HostObject == host.gameObject)
                        s_HostObject = null;
                    UnityEngine.Object.Destroy(host.gameObject);
                }
            }

            if (request.ExitEditor)
                SessionState.SetInt(ExitKey, result != null && result.Succeeded ? 0 : 1);

            if (exitPlayModeWhenDone && EditorApplication.isPlaying)
                EditorApplication.isPlaying = false;
        }

        static IEnumerator CaptureRoutine(PendingRequest request, Camera liveCamera, Action<Result> onComplete)
        {
            var hasGraphics = SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;
            var result = new Result
            {
                OutputFolder = request.OutputFolder,
                GraphicsAvailable = hasGraphics
            };

            Directory.CreateDirectory(request.OutputFolder);
            foreach (var old in Directory.GetFiles(request.OutputFolder))
                File.Delete(old);

            if (!hasGraphics)
                Debug.LogWarning("TlFrameRecorder: no graphics device (GraphicsDeviceType.Null); writing ticks.csv only and skipping PNG pictures. Run without -nographics for pictures.");

            var tickRate = (int)Math.Max(1u, TlBaking.TicksPerSecond);
            var asyncShaders = ShaderUtil.allowAsyncCompilation;
            var world = World.DefaultGameObjectInjectionWorld;
            FixedStepSimulationSystemGroup fixedGroup = null;
            var hadFixedStep = false;
            var previousTimestep = 0f;
            EntityManager entities = default;
            EntityQuery query = default;
            var queryCreated = false;
            RenderTexture target = null;
            Texture2D texture = null;
            var csv = new StringBuilder("frame,tick,x,y,z\n");
            var frame = 0;

            try
            {
                if (world == null || !world.IsCreated)
                {
                    result.Error = "Default ECS world is not available.";
                    Debug.LogError("TlFrameRecorder: " + result.Error);
                    onComplete(result);
                    yield break;
                }

                if (hasGraphics)
                {
                    ShaderUtil.allowAsyncCompilation = false;
                    target = new RenderTexture(request.Width, request.Height, 24, RenderTextureFormat.ARGB32)
                    {
                        antiAliasing = 1,
                        useMipMap = false
                    };
                    texture = new Texture2D(request.Width, request.Height, TextureFormat.RGB24, false);
                }

                Time.captureFramerate = tickRate;
                s_CaptureRateActive = true;

                fixedGroup = world.GetExistingSystemManaged<FixedStepSimulationSystemGroup>();
                if (fixedGroup != null && tickRate != 60)
                {
                    hadFixedStep = true;
                    previousTimestep = fixedGroup.Timestep;
                    fixedGroup.Timestep = 1f / tickRate;
                }

                entities = world.EntityManager;
                query = entities.CreateEntityQuery(ComponentType.ReadOnly<Playback>());
                queryCreated = true;

                var camera = ResolveCamera(liveCamera, request.CameraPath);
                var waitFrame = hasGraphics && !Application.isBatchMode ? (object)new WaitForEndOfFrame() : null;
                var deadline = Time.realtimeSinceStartup + request.TimeoutSeconds;

                while (Time.realtimeSinceStartup < deadline && frame < request.Frames)
                {
                    yield return waitFrame;

                    if (!world.IsCreated)
                    {
                        result.Error = "Default ECS world was destroyed during recording.";
                        Debug.LogError("TlFrameRecorder: " + result.Error);
                        break;
                    }

                    if (query.IsEmpty)
                        continue;

                    if (!TryReadFirstPlaying(entities, query, out var entity, out var tick))
                        continue;

                    var position = entities.HasComponent<LocalTransform>(entity)
                        ? entities.GetComponentData<LocalTransform>(entity).Position
                        : float3.zero;

                    if (frame % request.Every == 0)
                    {
                        if (hasGraphics)
                        {
                            if (camera == null)
                                camera = ResolveCamera(liveCamera, request.CameraPath);
                            if (camera == null)
                            {
                                result.Error = "No camera found to render frames.";
                                Debug.LogError("TlFrameRecorder: " + result.Error);
                                break;
                            }

                            var prevTarget = camera.targetTexture;
                            var prevActive = RenderTexture.active;
                            try
                            {
                                camera.targetTexture = target;
                                camera.Render();
                                RenderTexture.active = target;
                                texture.ReadPixels(new Rect(0, 0, request.Width, request.Height), 0, 0);
                                texture.Apply(false, false);
                            }
                            finally
                            {
                                camera.targetTexture = prevTarget;
                                RenderTexture.active = prevActive;
                            }

                            var pngName = "tick_" + tick.ToString("D4", CultureInfo.InvariantCulture) + ".png";
                            File.WriteAllBytes(Path.Combine(request.OutputFolder, pngName), texture.EncodeToPNG());
                            result.PicturesWritten++;
                        }

                        csv.Append(string.Format(
                            CultureInfo.InvariantCulture,
                            "{0},{1},{2:0.0000},{3:0.0000},{4:0.0000}\n",
                            frame,
                            tick,
                            position.x,
                            position.y,
                            position.z));
                        result.RowsWritten++;
                    }

                    frame++;
                }

                result.FramesRecorded = frame;
                File.WriteAllText(Path.Combine(request.OutputFolder, "ticks.csv"), csv.ToString());

                if (string.IsNullOrEmpty(result.Error) && frame < request.Frames)
                {
                    result.Error = "Recorded " + frame + " of " + request.Frames + " frames before timeout.";
                    Debug.LogError("TlFrameRecorder: " + result.Error);
                }

                if (string.IsNullOrEmpty(result.Error))
                {
                    result.Succeeded = true;
                    Debug.Log(string.Format(
                        CultureInfo.InvariantCulture,
                        "TlFrameRecorder: recorded {0} frames ({1} csv rows, {2} pngs) at {3} ticks/s to {4}.",
                        result.FramesRecorded,
                        result.RowsWritten,
                        result.PicturesWritten,
                        tickRate,
                        result.OutputFolder));
                }
            }
            finally
            {
                ResetCaptureFramerate();
                if (hasGraphics)
                    ShaderUtil.allowAsyncCompilation = asyncShaders;
                if (hadFixedStep && fixedGroup != null && world != null && world.IsCreated)
                    fixedGroup.Timestep = previousTimestep;
                if (queryCreated && world != null && world.IsCreated)
                    query.Dispose();
                if (texture != null)
                    UnityEngine.Object.Destroy(texture);
                if (target != null)
                {
                    target.Release();
                    UnityEngine.Object.Destroy(target);
                }
            }

            onComplete(result);
        }

        static unsafe bool TryReadFirstPlaying(EntityManager entities, EntityQuery query, out Entity entity, out int tick)
        {
            entity = Entity.Null;
            tick = 0;
            using var found = query.ToEntityArray(Allocator.Temp);
            var slots = stackalloc global::Tl.Playback[KindSlots.MaxSlots];
            for (var i = 0; i < found.Length; i++)
            {
                var count = TimelineTicks.Read(entities, found[i], slots, KindSlots.MaxSlots);
                for (var s = 0; s < count; s++)
                {
                    if (slots[s].Timeline != 0 && (slots[s].Flags & global::Tl.PlaybackFlags.StartPending) == 0)
                    {
                        entity = found[i];
                        tick = slots[s].Tick;
                        return true;
                    }
                }
            }
            return false;
        }
    }
}
