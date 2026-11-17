using System;
using System.Collections;
using MelonLoader;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using Valve.VR;
using Mathf = UnityEngine.Mathf;

namespace SonsVRMod
{
    /// <summary>
    /// Single source of truth for the OpenVR device poses of the current frame.
    /// <para>
    /// <see cref="WaitAndUpdate"/> must be called exactly once per frame, as early as possible
    /// (see <c>SonsVRMod.OnUpdate</c>). It performs the blocking <c>WaitGetPoses</c> call, which both
    /// throttles the game to the cadence of the headset and hands us the predicted pose for the frame
    /// we are about to render.
    /// </para>
    /// <para>
    /// Every consumer then reads the same cached snapshot, so the head transform, the eye cameras and
    /// the game camera can never end up one frame apart from each other. The previous implementation
    /// called <c>WaitGetPoses</c> from <c>StereoRender.LateUpdate</c>, whose ordering against
    /// <c>VRPlayer.LateUpdate</c> is undefined by Unity, which produced a mix of current and stale
    /// poses inside a single frame.
    /// </para>
    /// </summary>
    internal static class VRFrame
    {
        private static readonly TrackedDevicePose_t[] RenderPoses =
            new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];

        private static readonly TrackedDevicePose_t[] GamePoses =
            new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];

        /// <summary>Frame number the cached poses belong to (guards against double updates).</summary>
        public static int PoseFrame { get; private set; } = -1;

        /// <summary>True when the compositor reported a usable HMD pose this frame.</summary>
        public static bool HmdPoseValid { get; private set; }

        /// <summary>HMD position in tracking space, already converted to the left-handed Unity axes.</summary>
        public static Vector3 HmdPosition { get; private set; } = Vector3.zero;

        /// <summary>HMD orientation in tracking space, already converted to the left-handed Unity axes.</summary>
        public static Quaternion HmdRotation { get; private set; } = Quaternion.identity;

        private static int _errorLogCooldown;

        /// <summary>
        /// Blocks until the running start of the compositor, then caches the poses of this frame.
        /// Safe to call more than once per frame: extra calls are ignored.
        /// </summary>
        private static int _traceCount;


        /// <summary>
        /// Restarts the trace budget. The startup frames burn it long before the rig exists, which
        /// leaves the interesting frames, the ones around the first submit, completely untraced.
        /// </summary>
        public static void ArmTrace()
        {
            _traceCount = 0;
        }

        public static void WaitAndUpdate()
        {
            if (OpenVR.Compositor == null || OpenVR.System == null)
                return;

            if (!VRConfig.WaitForPoses)
                return;

            // WaitGetPoses must run once and only once per frame, otherwise we stall twice.
            if (PoseFrame == Time.frameCount)
                return;

            PoseFrame = Time.frameCount;

            // WaitGetPoses blocks. If the compositor never releases it the game hangs here with no
            // other symptom, so the first frames are traced on both sides of the call.
            bool trace = _traceCount < VRConfig.TraceFrames;
            if (trace)
            {
                _traceCount++;
                MelonLogger.Msg($"[VRFrame] WaitGetPoses enter (frame {Time.frameCount})");
            }

            VRWatchdog.Stage(VRWatchdog.WaitingForPoses);
            EVRCompositorError error = OpenVR.Compositor.WaitGetPoses(RenderPoses, GamePoses);
            VRWatchdog.Stage(VRWatchdog.Idle);

            if (trace)
                MelonLogger.Msg($"[VRFrame] WaitGetPoses exit -> {error}");
            if (error != EVRCompositorError.None)
            {
                if (--_errorLogCooldown <= 0)
                {
                    _errorLogCooldown = 300;
                    MelonLogger.Warning($"[VRFrame] WaitGetPoses returned {error}");
                }
                return;
            }

            TrackedDevicePose_t hmd = RenderPoses[(int)OpenVR.k_unTrackedDeviceIndex_Hmd];
            HmdPoseValid = hmd.bPoseIsValid && hmd.bDeviceIsConnected;

            if (HmdPoseValid)
            {
                HmdPosition = hmd.mDeviceToAbsoluteTracking.ToUnityPosition();
                HmdRotation = hmd.mDeviceToAbsoluteTracking.ToUnityRotation();
            }
        }

        private static readonly TrackedDevicePose_t[] RawPoses =
            new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];

        /// <summary>
        /// Head yaw in the raw, uncalibrated tracking universe.
        /// <para>
        /// The cached pose above comes from the compositor, which reports in the standing universe,
        /// while controller poses are read raw. Those two universes differ by the room calibration,
        /// so a yaw taken from one cannot be compared against a direction taken from the other. This
        /// gives callers a head yaw in the same space the hands live in.
        /// </para>
        /// </summary>
        public static bool TryGetRawHmdYaw(out float yaw)
        {
            yaw = 0f;

            if (OpenVR.System == null)
                return false;

            OpenVR.System.GetDeviceToAbsoluteTrackingPose(
                ETrackingUniverseOrigin.TrackingUniverseRawAndUncalibrated, 0f, RawPoses);

            TrackedDevicePose_t hmd = RawPoses[(int)OpenVR.k_unTrackedDeviceIndex_Hmd];
            if (!hmd.bPoseIsValid)
                return false;

            yaw = hmd.mDeviceToAbsoluteTracking.ToUnityRotation().eulerAngles.y;
            return true;
        }

        /// <summary>Raw render pose of a tracked device, for callers that need more than the HMD.</summary>
        public static TrackedDevicePose_t GetRenderPose(int deviceIndex)
        {
            return RenderPoses[deviceIndex];
        }
    }

    /// <summary>
    /// Owns the VR camera rig: the head transform, the two eye cameras, their render targets and the
    /// submission of those targets to the OpenVR compositor.
    /// <para>
    /// The component is deliberately passive: it never decides where the rig is in the world and it
    /// never runs its own <c>LateUpdate</c>. <see cref="VRPlayer"/> drives it through
    /// <see cref="ApplyHmdPose"/> so that pose application, rig placement and game camera sync happen
    /// in one deterministic order inside a single frame.
    /// </para>
    /// <para>
    /// Frame contract: <c>WaitGetPoses</c> (early update) then <see cref="ApplyHmdPose"/> (late update)
    /// then Unity renders the eye cameras then <see cref="StereoRenderPass.Execute"/> at end of frame.
    /// Submitting before the cameras have rendered, as the old code did from
    /// <c>MelonMod.OnLateUpdate</c>, always presented the image of the previous frame.
    /// </para>
    /// </summary>
    /// <summary>How the desktop window is fed while the headset is being rendered.</summary>
    internal enum VRMirrorMode
    {
        /// <summary>
        /// The game camera is stripped down to nothing and the desktop shows a copy of the left eye.
        /// Cheapest: the frame costs two eye renders instead of three full renders.
        /// </summary>
        BlitLeftEye = 0,

        /// <summary>The game camera keeps rendering the world on screen. Costs a third full render.</summary>
        GameCamera = 1
    }

    internal class StereoRender : MonoBehaviour
    {
        public StereoRender(IntPtr value) : base(value) { }

        /// <summary>Singleton instance for global access.</summary>
        public static StereoRender Instance;

        /// <summary>
        /// HMD transform. Its local pose is the raw tracked pose (full 6DoF); its parent chain carries
        /// the rig placement decided by <see cref="VRPlayer"/>.
        /// </summary>
        public Transform Head;

        /// <summary>The game camera the rig currently mirrors (source of the render settings).</summary>
        public Camera HeadCam;

        /// <summary>Per-eye cameras. Their local pose comes from the OpenVR eye-to-head transforms.</summary>
        public Camera LeftCam, RightCam;

        /// <summary>Per-eye render targets handed to the compositor.</summary>
        public RenderTexture LeftRT, RightRT;

        /// <summary>Near clipping plane in meters. Must match the value fed to GetProjectionMatrix.</summary>
        public float clipStart = 0.02f;

        /// <summary>Far clipping plane in meters. Must match the value fed to GetProjectionMatrix.</summary>
        public float clipEnd = 24000f;

        /// <summary>
        /// Multiplier applied to the recommended eye resolution of the runtime.
        /// <para>
        /// Deliberately below 1. The size SteamVR recommends already includes its own supersampling
        /// setting, and rendering the full HDRP stack twice at that size is what the frame budget
        /// actually has to pay. Raise it once the frame time is known to have headroom.
        /// </para>
        /// </summary>
        public float renderScale = 0.7f;

        /// <summary>
        /// Whether the eye cameras keep their HDRP temporal history alive between frames.
        /// <para>
        /// It removes flicker in temporal effects, but HDRP allocates a full set of history buffers
        /// per camera (anti-aliasing, motion vectors, screen space reflections, volumetric
        /// accumulation). At eye resolution, across two extra cameras, that is a large amount of
        /// video memory, so it stays off until the frame is known to fit comfortably.
        /// </para>
        /// </summary>
        public bool keepTemporalHistory = false;

        /// <summary>
        /// MSAA sample count of the eye targets. HDRP resolves anti-aliasing itself on a deferred
        /// path, so more than one sample here usually costs memory without changing the image.
        /// </summary>
        public int msaaSamples = 1;

        /// <summary>
        /// Culling mask the eye cameras fall back to before a game camera has been bound. Once
        /// <see cref="BindGameCamera"/> runs, the mask of the game camera is used instead.
        /// </summary>
        public static int defaultCullingMask = -1;

        /// <summary>Submission pass. Public for compatibility with the existing call sites.</summary>
        public StereoRenderPass stereoRenderPass;

        /// <summary>
        /// How the desktop window is fed. <see cref="VRMirrorMode.BlitLeftEye"/> removes a whole
        /// render pass from the frame; switch to <see cref="VRMirrorMode.GameCamera"/> if some game
        /// system turns out to need the camera to actually draw the world.
        /// </summary>
        public VRMirrorMode mirrorMode = VRMirrorMode.BlitLeftEye;

        /// <summary>
        /// Size of the dummy target the neutralised game camera renders into.
        /// Its full-screen passes (tonemapping, bloom, TAA) run at this size instead of at the
        /// desktop resolution, which is where the remaining cost of that camera would otherwise sit.
        /// </summary>
        public int mirrorCameraWidth = 256, mirrorCameraHeight = 144;

        private RenderTexture _mirrorDummyRT;
        private RenderTexture _mirrorViewRT;
        private Camera _neutralisedCamera;
        private bool _mirrorActive;
        private int _savedCullingMask;
        private RenderTexture _savedTargetTexture;
        private bool _savedAllowDynamicResolution;
        private float _savedDepth;

        private Transform _leftEye, _rightEye;
        private Matrix4x4 _leftProjection = Matrix4x4.identity;
        private Matrix4x4 _rightProjection = Matrix4x4.identity;
        private int _currentWidth, _currentHeight;
        private int _calibrationCountdown;
        private bool _ready;
        private bool _geometryValid;
        private bool _renderingActive = true;
        private Vector3 _headNeutral = Vector3.zero;

        /// <summary>
        /// The tracked head position that corresponds to the player standing normally. Head movement
        /// is measured from here, so the rig origin can sit exactly on the head of the character.
        /// </summary>
        public void SetHeadNeutral(Vector3 neutral)
        {
            _headNeutral = neutral;
        }

        /// <summary>
        /// False while the rig has nothing to follow, which is the window a scene load sits in.
        /// The whole VR frame loop stands down in that state: no blocking wait on the compositor,
        /// no submits.
        /// </summary>
        public bool RenderingActive => _renderingActive;

        // Frame of the last submission done by the end-of-frame coroutine. While it keeps advancing,
        // the late-update fallback stays out of the way.
        private int _lastEndOfFrameSubmit = -1;

        /// <summary>How often, in frames, eye offsets and projections are re-read from OpenVR.</summary>
        private const int CalibrationInterval = 90;

        /// <summary>Frames the end-of-frame coroutine gets before the late update fallback engages.</summary>
        private const int FallbackGraceFrames = 60;

        private bool _fallbackWarned;

        public void Awake()
        {
            Instance = this;

            renderScale = VRConfig.RenderScale;
            keepTemporalHistory = VRConfig.KeepTemporalHistory;
            mirrorMode = VRConfig.DesktopMirror ? VRMirrorMode.BlitLeftEye : VRMirrorMode.GameCamera;

            Setup();
            MelonLoader.MelonCoroutines.Start(SubmitAtEndOfFrame());
        }

        public void OnDestroy()
        {
            RestoreNeutralisedCamera();
            ReleaseTargets();

            if (_mirrorDummyRT != null)
            {
                _mirrorDummyRT.Release();
                Destroy(_mirrorDummyRT);
                _mirrorDummyRT = null;
            }

            if (_mirrorViewRT != null)
            {
                _mirrorViewRT.Release();
                Destroy(_mirrorViewRT);
                _mirrorViewRT = null;
            }

            if (Instance == this)
                Instance = null;
        }

        /// <summary>
        /// Builds the rig (Head, then LeftEye and RightEye), reads the headset geometry from OpenVR
        /// and allocates the eye render targets.
        /// </summary>
        public void Setup()
        {
            MelonLogger.Msg("[StereoRender] ### SETTING UP STEREO RENDERING PIPELINE ###");

            Head = transform.Find("VRHead");
            if (!Head)
                Head = new GameObject("VRHead").transform;

            Head.SetParent(transform, false);
            Head.localPosition = Vector3.zero;
            Head.localRotation = Quaternion.identity;
            Head.localScale = Vector3.one;

            _leftEye = CreateEye("VRLeftEye", ref LeftCam);
            _rightEye = CreateEye("VRRightEye", ref RightCam);

            RefreshHeadsetGeometry();
            UpdateResolution();

            stereoRenderPass = new StereoRenderPass(this);
            _ready = true;
        }

        private Transform CreateEye(string eyeName, ref Camera cam)
        {
            Transform eye = Head.Find(eyeName);
            if (!eye)
                eye = new GameObject(eyeName).transform;

            eye.SetParent(Head, false);
            eye.localPosition = Vector3.zero;
            eye.localRotation = Quaternion.identity;
            eye.localScale = Vector3.one;

            cam = eye.gameObject.GetOrAddComponent<Camera>();
            cam.stereoTargetEye = StereoTargetEyeMask.None; // stereo is driven manually, not by Unity XR
            cam.cullingMask = defaultCullingMask;
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.rect = new Rect(0f, 0f, 1f, 1f);
            cam.nearClipPlane = clipStart;
            cam.farClipPlane = clipEnd;
            cam.depth = -100f; // render before the on-screen camera of the game
            cam.enabled = VRConfig.EyeCamerasEnabled;

            return eye;
        }

        /// <summary>
        /// Copies the render configuration of the active game camera onto both eye cameras.
        /// <para>
        /// This is what keeps the VR view visually identical to the flat view: culling mask, clear
        /// flags, background color, HDR, occlusion culling and rendering path all come from the game.
        /// The old code hard-coded <c>cullingMask = -1</c> and a fixed 109 degree FoV, so it rendered
        /// layers the game hides and ignored the real projection of the headset.
        /// </para>
        /// </summary>
        public void BindGameCamera(Camera source)
        {
            if (source == null || LeftCam == null || RightCam == null)
                return;

            // Give back whatever camera we stripped down previously, so the settings we are about to
            // copy are the ones the game set and not the ones we forced for the mirror.
            RestoreNeutralisedCamera();

            HeadCam = source;

            // Read the mask before anything else touches the camera. A camera that renders nothing
            // would make the eye cameras render nothing either, which reaches the headset as a black
            // image, so fall back to everything rather than inherit an empty mask.
            int sourceMask = source.cullingMask;
            defaultCullingMask = sourceMask != 0 ? sourceMask : -1;

            ApplyGameCameraSettings(LeftCam, source);
            ApplyGameCameraSettings(RightCam, source);

            ApplyMirrorMode();

            // CopyFrom wipes the custom projection and the viewport, so both come back afterwards.
            // The viewport goes first: assigning rect is itself what discards a custom projection
            // matrix, so doing it second would throw away the projection just restored.
            ApplyEyeViewports();
            ApplyProjections();
            ApplyRenderTargets();

            // From here the eye cameras start rendering and submitting; re-arm the pose trace so the
            // frames that matter are covered.
            VRFrame.ArmTrace();

            MelonLogger.Msg($"[StereoRender] Graphics: {SystemInfo.graphicsDeviceType}, " +
                            $"multithreaded {SystemInfo.graphicsMultiThreaded}");

            MelonLogger.Msg($"[StereoRender] Bound to game camera '{source.name}' " +
                            $"(source mask 0x{sourceMask:X} -> eyes 0x{defaultCullingMask:X}, " +
                            $"clear {source.clearFlags})");
        }

        private void ApplyGameCameraSettings(Camera eyeCam, Camera source)
        {
            eyeCam.CopyFrom(source);
            CopyHDCameraData(source, eyeCam);

            // Re-assert everything the VR pipeline owns and CopyFrom just overwrote.
            eyeCam.cullingMask = defaultCullingMask;
            eyeCam.stereoTargetEye = StereoTargetEyeMask.None;
            eyeCam.rect = new Rect(0f, 0f, 1f, 1f);
            eyeCam.usePhysicalProperties = false;
            eyeCam.nearClipPlane = clipStart;
            eyeCam.farClipPlane = clipEnd;
            eyeCam.depth = -100f;
            eyeCam.enabled = VRConfig.EyeCamerasEnabled;
        }

        /// <summary>
        /// Effects that simulate a camera rather than an eye. In a headset they fight what the eye is
        /// already doing, so switching them off improves comfort and speed at the same time.
        /// </summary>
        private static readonly FrameSettingsField[] CameraEffectFields =
        {
            FrameSettingsField.MotionBlur,
            FrameSettingsField.DepthOfField,
            FrameSettingsField.Distortion,
            FrameSettingsField.RoughDistortion,
            FrameSettingsField.ChromaticAberration
        };

        /// <summary>
        /// Screen space effects. Expensive, and each eye derives them from its own view, so the two
        /// eyes disagree about them, which reads badly in stereo.
        /// </summary>
        private static readonly FrameSettingsField[] ScreenSpaceFields =
        {
            FrameSettingsField.SSR,
            FrameSettingsField.TransparentSSR,
            FrameSettingsField.SSAO,
            FrameSettingsField.ContactShadows
        };

        private static readonly FrameSettingsField[] VolumetricFields =
        {
            FrameSettingsField.Volumetrics,
            FrameSettingsField.ReprojectionForVolumetrics,
            FrameSettingsField.VolumetricClouds
        };

        /// <summary>
        /// Gives an eye camera its own, lighter rendering profile.
        /// <para>
        /// The pipeline itself cannot be swapped for a cheaper one: it is chosen globally and every
        /// material in the game is compiled for this one. What can be done is to tell it, per camera,
        /// which passes to skip. Each field needs two bits set, one saying the camera overrides the
        /// setting and one carrying the value, which is why the override mask is written alongside.
        /// </para>
        /// </summary>
        private void ApplyEyeFrameSettings(HDAdditionalCameraData data, string cameraName)
        {
            if (!VRConfig.EyeDisableCameraEffects &&
                !VRConfig.EyeDisableScreenSpaceEffects &&
                !VRConfig.EyeDisableVolumetrics)
                return;

            try
            {
                FrameSettings settings = data.renderingPathCustomFrameSettings;
                FrameSettingsOverrideMask overrides = data.renderingPathCustomFrameSettingsOverrideMask;

                int count = 0;
                count += DisableFields(ref settings, ref overrides, CameraEffectFields, VRConfig.EyeDisableCameraEffects);
                count += DisableFields(ref settings, ref overrides, ScreenSpaceFields, VRConfig.EyeDisableScreenSpaceEffects);
                count += DisableFields(ref settings, ref overrides, VolumetricFields, VRConfig.EyeDisableVolumetrics);

                if (count == 0)
                    return;

                data.renderingPathCustomFrameSettings = settings;
                data.renderingPathCustomFrameSettingsOverrideMask = overrides;
                data.customRenderingSettings = true;

                MelonLogger.Msg($"[StereoRender] {cameraName}: {count} rendering passes switched off");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[StereoRender] Could not adjust the rendering profile: {e.Message}");
            }
        }

        private static int DisableFields(ref FrameSettings settings, ref FrameSettingsOverrideMask overrides,
            FrameSettingsField[] fields, bool enabled)
        {
            if (!enabled)
                return 0;

            int count = 0;
            foreach (FrameSettingsField field in fields)
            {
                try
                {
                    settings.SetEnabled(field, false);
                    overrides.mask[(uint)field] = true;
                    count++;
                }
                catch
                {
                    // A field this build of the pipeline does not carry: skip it rather than give up
                    // on the whole set.
                }
            }

            return count;
        }

        /// <summary>
        /// Strips the game camera down so it stops costing a third full render of the world.
        /// <para>
        /// Two things are done, and both matter for a different reason. <c>cullingMask = 0</c> means
        /// the camera sees no renderer and therefore no shadow casting light, so HDRP skips geometry
        /// and, more importantly, skips rendering the shadow atlas for it. That cost is driven by the
        /// scene, not by the pixel count, so shrinking the camera alone would not have touched it.
        /// The tiny target texture then takes care of what is left: the full-screen passes
        /// (tonemapping, bloom, temporal anti-aliasing) run at 256x144 instead of at desktop
        /// resolution.
        /// </para>
        /// <para>
        /// The camera stays enabled on purpose. Disabling it would take it out of
        /// <c>Camera.allCameras</c>, which is what <see cref="VRSystem"/> scans, and the rig would
        /// immediately start looking for a different camera to follow.
        /// </para>
        /// </summary>
        /// <summary>
        /// Enables or disables the mirror optimisation. It must stay off anywhere the game camera is
        /// the only thing producing an image, the main menu above all, otherwise the desktop goes
        /// black and the player cannot even reach the game.
        /// </summary>
        public void SetMirrorActive(bool active)
        {
            if (_mirrorActive == active)
                return;

            _mirrorActive = active;
            ApplyMirrorMode();
        }

        public void ApplyMirrorMode()
        {
            if (mirrorMode != VRMirrorMode.BlitLeftEye || !_mirrorActive || HeadCam == null)
            {
                RestoreNeutralisedCamera();
                return;
            }

            if (VRConfig.MirrorTinyTarget && _mirrorDummyRT == null)
            {
                _mirrorDummyRT = new RenderTexture(
                    Mathf.Max(16, mirrorCameraWidth), Mathf.Max(16, mirrorCameraHeight),
                    24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
                {
                    name = "VRMirrorDummyRT"
                };
                _mirrorDummyRT.Create();
            }

            if (_neutralisedCamera != HeadCam)
            {
                RestoreNeutralisedCamera();

                _neutralisedCamera = HeadCam;
                _savedCullingMask = HeadCam.cullingMask;
                _savedAllowDynamicResolution = HeadCam.allowDynamicResolution;
                _savedTargetTexture = HeadCam.targetTexture;
                _savedDepth = HeadCam.depth;
            }

            EnforceMirrorCamera();
        }

        /// <summary>
        /// Re-applies the stripped down state. Called every frame because game code is free to reset
        /// the culling mask or the target texture of its own camera at any point.
        /// </summary>
        private void EnforceMirrorCamera()
        {
            if (_neutralisedCamera == null || !_mirrorActive || mirrorMode != VRMirrorMode.BlitLeftEye)
                return;

            // An empty culling mask is where the saving comes from: no geometry to cull and no
            // shadow casting light in view, so the whole shadow atlas pass is skipped for this
            // camera. That is processor time, and the processor is what limits this frame.
            //
            // The resolution is deliberately left alone. Pointing this camera at a tiny target used
            // to look like free extra savings, but it resizes the render targets the pipeline shares
            // between cameras, and it renders last: the next frame then began with an eye camera
            // drawing at the wrong scale, which showed up as a black eye with the sun and its flare
            // still painted over it. The pixels were never the problem anyway.
            if (_neutralisedCamera.cullingMask != 0)
                _neutralisedCamera.cullingMask = 0;

            if (_neutralisedCamera.allowDynamicResolution)
                _neutralisedCamera.allowDynamicResolution = false;

            if (!VRConfig.MirrorTinyTarget)
                return;

            // The empty culling mask removes the geometry and the shadow atlas, but the full screen
            // passes still run at desktop resolution, and measuring showed those pixels are most of
            // what this camera costs. A tiny target removes them too.
            //
            // It has to render FIRST, hence the depth below the eye cameras. Rendering last, it left
            // the render targets the pipeline shares between cameras sized for 256x144, and the next
            // frame opened with an eye camera drawing at that scale: a black eye with the sun still
            // painted over it. Going first, the eye cameras run afterwards and set the size back.
            if (_neutralisedCamera.targetTexture != _mirrorDummyRT)
                _neutralisedCamera.targetTexture = _mirrorDummyRT;

            float firstDepth = LeftCam != null ? LeftCam.depth - 100f : -200f;
            if (_neutralisedCamera.depth != firstDepth)
                _neutralisedCamera.depth = firstDepth;
        }

        /// <summary>Gives the game camera back the settings it had before we stripped it.</summary>
        private void RestoreNeutralisedCamera()
        {
            if (_neutralisedCamera == null)
                return;

            _neutralisedCamera.cullingMask = _savedCullingMask;
            _neutralisedCamera.allowDynamicResolution = _savedAllowDynamicResolution;
            _neutralisedCamera.targetTexture = _savedTargetTexture;
            _neutralisedCamera.depth = _savedDepth;
            _neutralisedCamera = null;
        }

        /// <summary>
        /// Takes the copy of the left eye that the desktop mirror draws from.
        /// <para>
        /// Done after the submit, so the compositor always receives the eye target untouched. It is
        /// also downscaled: the desktop has no use for eye resolution, and copying less costs less.
        /// </para>
        /// </summary>
        private void CaptureMirrorView()
        {
            if (mirrorMode != VRMirrorMode.BlitLeftEye || !_mirrorActive)
                return;

            // Which eye feeds the desktop is a setting purely so the fault can be told apart: if the
            // dead eye follows this choice, the copy is what kills it; if it stays on the left, the
            // cause is that the left camera is simply the first one rendered each frame.
            RenderTexture source = VRConfig.MirrorRightEye ? RightRT : LeftRT;

            if (source == null || !source.IsCreated())
                return;

            if (_mirrorViewRT == null)
            {
                int width = Mathf.Clamp(Screen.width, 320, 1280);
                int height = Mathf.Max(180, Mathf.RoundToInt(width * (float)source.height / source.width));

                _mirrorViewRT = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32,
                    RenderTextureReadWrite.Linear)
                {
                    name = "VRMirrorViewRT",
                    filterMode = FilterMode.Bilinear
                };
                _mirrorViewRT.Create();
            }

            VRWatchdog.Stage(VRWatchdog.MirrorCapture);
            Graphics.Blit(source, _mirrorViewRT);
            VRWatchdog.Stage(VRWatchdog.Idle);

            // Blit leaves its destination bound as the active target. The eye cameras are the first
            // thing rendered next frame, so anything left dangling here lands on them, and on the
            // left one first of all.
            RenderTexture.active = null;
        }

        /// <summary>
        /// Draws the left eye onto the desktop window. With the game camera neutralised nothing else
        /// writes to the back buffer, so this is the entire desktop image.
        /// ScaleToFit is used because an eye target is far from the aspect ratio of the monitor.
        /// </summary>
        public void OnGUI()
        {
            if (mirrorMode != VRMirrorMode.BlitLeftEye || !_mirrorActive)
                return;

            if (Event.current == null || Event.current.type != EventType.Repaint)
                return;

            // Drawn from a copy, never from the eye target itself.
            //
            // OnGUI runs before the end of frame submit, so drawing the eye target here would bind it
            // as a source and invalidate its contents before the compositor ever sees it: the left
            // eye went black in the headset while the right one, which nothing else touched, was
            // fine. The copy is taken after the submit, so what the desktop shows is one frame old,
            // which nobody watching a monitor will notice.
            if (_mirrorViewRT == null || !_mirrorViewRT.IsCreated())
                return;

            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), _mirrorViewRT,
                ScaleMode.ScaleToFit, false);
        }

        /// <summary>
        /// Mirrors the HDRP-specific configuration of the game camera onto an eye camera.
        /// <para>
        /// <c>Camera.CopyFrom</c> only knows about the built-in Camera fields. Everything HDRP cares
        /// about lives on a separate <c>HDAdditionalCameraData</c> component: volume layer mask and
        /// anchor, probe layer mask, clear mode, anti-aliasing mode, dynamic resolution and the custom
        /// frame settings. Without this the eye cameras render with HDRP defaults, so the VR view
        /// loses the post-processing and volume setup the flat view has.
        /// </para>
        /// <para>
        /// <c>hasPersistentHistory</c> is forced on: HDRP keeps its temporal history (TAA, motion
        /// vectors, screen space reflections, volumetric accumulation) per Camera instance, and an eye
        /// camera that is not marked persistent can have that history dropped between frames, which
        /// shows up as flicker and ghosting in the headset.
        /// </para>
        /// </summary>
        private void CopyHDCameraData(Camera source, Camera eyeCam)
        {
            try
            {
                var sourceData = source.GetComponent<HDAdditionalCameraData>();
                if (sourceData == null)
                    return;

                var eyeData = eyeCam.gameObject.GetComponent<HDAdditionalCameraData>();
                if (eyeData == null)
                    eyeData = eyeCam.gameObject.AddComponent<HDAdditionalCameraData>();

                sourceData.CopyTo(eyeData);
                eyeData.hasPersistentHistory = keepTemporalHistory;

                ApplyEyeFrameSettings(eyeData, eyeCam.name);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[StereoRender] HDRP camera data not mirrored: {e.Message}");
            }
        }

        /// <summary>
        /// Re-reads the headset geometry from OpenVR: the per-eye offsets from the head
        /// (<c>GetEyeToHeadTransform</c>) and the per-eye projection matrices.
        /// <para>
        /// The eye offsets are the real values of the runtime, which include the IPD of the user, any
        /// vertical offset and the lens canting of the headset. Hard-coding plus or minus 0.032 on X,
        /// as the old code did, contradicted the asymmetric projection matrices it was paired with and
        /// produced wrong stereo disparity and a wrong sense of world scale.
        /// </para>
        /// </summary>
        public void RefreshHeadsetGeometry()
        {
            if (OpenVR.System == null || _leftEye == null || _rightEye == null)
                return;

            HmdMatrix34_t leftToHead = OpenVR.System.GetEyeToHeadTransform(EVREye.Eye_Left);
            HmdMatrix34_t rightToHead = OpenVR.System.GetEyeToHeadTransform(EVREye.Eye_Right);

            _leftEye.localPosition = leftToHead.ToUnityPosition();
            _leftEye.localRotation = leftToHead.ToUnityRotation();
            _rightEye.localPosition = rightToHead.ToUnityPosition();
            _rightEye.localRotation = rightToHead.ToUnityRotation();

            _leftProjection = BuildEyeProjection(EVREye.Eye_Left);
            _rightProjection = BuildEyeProjection(EVREye.Eye_Right);
            ApplyEyeViewports();

            // An identity projection renders nothing at all and reaches the headset as a pure black
            // image, so treat the geometry as usable only once the runtime has actually answered.
            bool wasValid = _geometryValid;
            _geometryValid = !Mathf.Approximately(_leftProjection.m00, 1f) ||
                             !Mathf.Approximately(_leftProjection.m11, 1f);

            if (!_geometryValid)
            {
                MelonLogger.Warning("[StereoRender] OpenVR returned an identity projection; " +
                                    "eye cameras stay on their default projection this pass");
            }
            else if (!wasValid)
            {
                float ipd = Vector3.Distance(_leftEye.localPosition, _rightEye.localPosition);
                MelonLogger.Msg($"[StereoRender] Headset geometry: ipd {ipd * 1000f:F1} mm, " +
                                $"left eye {_leftEye.localPosition}, " +
                                $"proj m00 {_leftProjection.m00:F3} m11 {_leftProjection.m11:F3} " +
                                $"m02 {_leftProjection.m02:F3} m12 {_leftProjection.m12:F3}");

                // Where a headset puts its canting decides whether anything downstream may rebuild
                // the projection from tangents. Some runtimes express it as a rotation of the eye,
                // others fold it into the projection as a shear; the two are logged side by side so
                // a headset that looks wrong can be told apart from one that merely looks unusual.
                Vector3 leftCant = _leftEye.localRotation.eulerAngles;
                Vector3 rightCant = _rightEye.localRotation.eulerAngles;
                MelonLogger.Msg($"[StereoRender] Eye canting: left {leftCant}, right {rightCant}, " +
                                $"projection shear left m01 {_leftProjection.m01:F4} m10 {_leftProjection.m10:F4}, " +
                                $"right m01 {_rightProjection.m01:F4} m10 {_rightProjection.m10:F4}");
            }

            ApplyProjections();

            if (!_opticsReported)
            {
                _opticsReported = true;
                ReportOptics();
            }
        }

        private bool _opticsReported;

        private static float CropX => Mathf.Clamp(VRConfig.FovTangentHorizontal, 0.4f, 1f);
        private static float CropY => Mathf.Clamp(VRConfig.FovTangentVertical, 0.4f, 1f);

        /// <summary>
        /// Builds the projection for one eye, narrowed by the field of view tangents.
        /// <para>
        /// The matrix always comes from the runtime, never from a frustum rebuilt out of the raw
        /// tangents. Four tangents describe an axis aligned off-axis frustum and nothing more, so
        /// <c>Matrix4x4.Frustum</c> can only ever produce an image plane perpendicular to the forward
        /// axis of the eye. A headset with canted displays puts a rotation into its projection, which
        /// that rebuild silently drops: the scene is then rendered onto a straight image plane and
        /// shown by the compositor on a tilted one, and the difference reaches the eye as a keystone
        /// that shears the world as the head turns. Headsets with parallel displays are unaffected,
        /// which is exactly why the fault only ever showed on the canted ones.
        /// </para>
        /// <para>
        /// The narrowing is therefore applied on top of the runtime matrix instead of replacing it.
        /// The crop is centred, so in clip space it is a plain scale: the sub-rectangle spans
        /// [-CropX, CropX] of normalised device x, and dividing by CropX stretches it back to the
        /// full [-1, 1] the viewport expects. Every other term of the runtime matrix, the canting
        /// included, survives untouched.
        /// </para>
        /// <para>
        /// This is why the result is not a zoom. The compositor still maps the whole texture onto the
        /// whole field of view; the part outside the sub-rectangle is simply black instead of scenery.
        /// </para>
        /// </summary>
        private Matrix4x4 BuildEyeProjection(EVREye eye)
        {
            Matrix4x4 projection = OpenVR.System.GetProjectionMatrix(eye, clipStart, clipEnd).ConvertToMatrix4x4();

            if (CropX >= 0.999f && CropY >= 0.999f)
                return projection;

            Matrix4x4 crop = Matrix4x4.identity;
            crop.m00 = 1f / CropX;
            crop.m11 = 1f / CropY;

            return crop * projection;
        }

        /// <summary>
        /// Restricts the eye cameras to the centred part of their target that the narrowed frustum
        /// corresponds to. Everything outside stays as the target was last cleared: black.
        /// </summary>
        private void ApplyEyeViewports()
        {
            float insetX = (1f - CropX) * 0.5f;
            float insetY = (1f - CropY) * 0.5f;
            var viewport = new Rect(insetX, insetY, CropX, CropY);

            // Only written when it actually differs. The setter discards the custom projection
            // matrix, and this runs every frame, so an unconditional assignment would spend every
            // frame invalidating a projection that was already right.
            if (LeftCam != null && LeftCam.rect != viewport) LeftCam.rect = viewport;
            if (RightCam != null && RightCam.rect != viewport) RightCam.rect = viewport;
        }

        /// <summary>
        /// Reports what the headset actually asks for, and how much of the rendered image its lenses
        /// never show.
        /// <para>
        /// The rendered image is larger than what reaches the eye, but most of that margin is not
        /// waste: the compositor warps it to match the lens, magnifying the centre and compressing
        /// the edges, so an outer band of the rectangle ends up as a thin rim of the view. Cropping
        /// it would put black at the edge of vision. What is genuinely wasted is the corner area the
        /// lens cannot reach, and the runtime describes it exactly, as a mesh.
        /// </para>
        /// </summary>
        private void ReportOptics()
        {
            if (OpenVR.System == null)
                return;

            try
            {
                uint recommendedWidth = 0, recommendedHeight = 0;
                OpenVR.System.GetRecommendedRenderTargetSize(ref recommendedWidth, ref recommendedHeight);

                float left = 0f, right = 0f, top = 0f, bottom = 0f;
                OpenVR.System.GetProjectionRaw(EVREye.Eye_Left, ref left, ref right, ref top, ref bottom);

                float horizontal = (Mathf.Atan(Mathf.Abs(left)) + Mathf.Atan(Mathf.Abs(right))) * Mathf.Rad2Deg;
                float vertical = (Mathf.Atan(Mathf.Abs(top)) + Mathf.Atan(Mathf.Abs(bottom))) * Mathf.Rad2Deg;

                MelonLogger.Msg($"[StereoRender] Optics: runtime asks {recommendedWidth}x{recommendedHeight} per eye, " +
                                $"field of view {horizontal:F1} x {vertical:F1} degrees, " +
                                $"rendering {_currentWidth}x{_currentHeight} (scale {renderScale:F2})");

                if (CropX < 0.999f || CropY < 0.999f)
                {
                    float pixels = CropX * CropY;
                    MelonLogger.Msg($"[StereoRender] Field of view tangents {CropX * 100f:F0}% x {CropY * 100f:F0}%: " +
                                    $"drawing {pixels * 100f:F0}% of the pixels, " +
                                    $"about {horizontal * CropX:F1} x {vertical * CropY:F1} degrees");
                }

                float hidden = MeasureHiddenArea(EVREye.Eye_Left);
                if (hidden > 0f)
                {
                    MelonLogger.Msg($"[StereoRender] Lenses never show {hidden * 100f:F1}% of the rendered image. " +
                                    $"That is the only part that can be skipped without losing anything; " +
                                    $"the rest of the margin is what the lens distortion spreads out.");
                }
                else
                {
                    MelonLogger.Msg("[StereoRender] The runtime reports no hidden area for this headset.");
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[StereoRender] Could not read the headset optics: {e.Message}");
            }
        }

        /// <summary>
        /// Fraction of the eye texture covered by the mesh the runtime marks as never visible.
        /// The mesh is given in texture space, so summing the area of its triangles gives the share
        /// of the image that is rendered and then thrown away by the optics.
        /// </summary>
        private static float MeasureHiddenArea(EVREye eye)
        {
            HiddenAreaMesh_t mesh = OpenVR.System.GetHiddenAreaMesh(eye, EHiddenAreaMeshType.k_eHiddenAreaMesh_Standard);

            if (mesh.pVertexData == IntPtr.Zero || mesh.unTriangleCount == 0)
                return 0f;

            float area = 0f;
            int vertexCount = (int)mesh.unTriangleCount * 3;

            for (int i = 0; i < vertexCount; i += 3)
            {
                Vector2 a = ReadVertex(mesh.pVertexData, i);
                Vector2 b = ReadVertex(mesh.pVertexData, i + 1);
                Vector2 c = ReadVertex(mesh.pVertexData, i + 2);

                area += Mathf.Abs((b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y)) * 0.5f;
            }

            return area;
        }

        private static Vector2 ReadVertex(IntPtr data, int index)
        {
            int offset = index * 8; // two floats per vertex
            float x = System.Runtime.InteropServices.Marshal.PtrToStructure<float>(data + offset);
            float y = System.Runtime.InteropServices.Marshal.PtrToStructure<float>(data + offset + 4);
            return new Vector2(x, y);
        }

        /// <summary>
        /// Pushes the cached projection matrices onto the eye cameras. Cheap, and re-applied every
        /// frame because touching fieldOfView, aspect or rect anywhere in the engine silently
        /// invalidates a custom projection matrix.
        /// </summary>
        private void ApplyProjections()
        {
            if (LeftCam == null || RightCam == null || !_geometryValid)
                return;

            LeftCam.projectionMatrix = _leftProjection;
            RightCam.projectionMatrix = _rightProjection;
        }

        /// <summary>
        /// Applies the HMD pose cached by <see cref="VRFrame"/> to the head transform, refreshes the
        /// projections and periodically re-reads the headset geometry. Called by <see cref="VRPlayer"/>,
        /// never by Unity, so the ordering is deterministic.
        /// </summary>
        public void ApplyHmdPose()
        {
            if (!_ready || Head == null)
                return;

            if (VRFrame.HmdPoseValid)
            {
                // Full 6DoF, but expressed as a displacement from the neutral standing pose rather
                // than as a raw position in the tracking space. The rig origin is placed on the head
                // of the character, so this displacement is exactly how far the real head has moved
                // away from it.
                Vector3 offset = VRFrame.HmdPosition - _headNeutral;

                // Clamped, so the view can never wander off the head of the character. Without this
                // a step to the side in the room, or plain tracking drift, walks the camera into the
                // body of the character and through whatever else is nearby.
                float range = Mathf.Max(0f, VRConfig.PositionalTrackingRange);
                if (offset.sqrMagnitude > range * range)
                    offset = offset.normalized * range;

                Head.localPosition = offset;
                Head.localRotation = VRFrame.HmdRotation;
            }

            // The narrowed projection and the viewport it was cut for only agree while both are in
            // place; anything in the engine that resets one leaves the other describing a field of
            // view the image no longer fills.
            ApplyEyeViewports();
            ApplyProjections();
            EnforceMirrorCamera();
            LogFrameHeartbeat();

            if (--_calibrationCountdown <= 0)
            {
                _calibrationCountdown = CalibrationInterval;
                RefreshHeadsetGeometry();
                CheckResolution();
            }
        }

        private int _heartbeatFrames;

        /// <summary>
        /// Reports the first frames after the rig goes live, then one frame in every hundred.
        /// A game that renders one frame every few seconds and a game that is genuinely hung look
        /// identical from the outside; this is what tells them apart in the log.
        /// </summary>
        private void LogFrameHeartbeat()
        {
            _heartbeatFrames++;

            bool firstFrames = _heartbeatFrames <= 5;
            if (!firstFrames && _heartbeatFrames % 100 != 0)
                return;

            float ms = Time.unscaledDeltaTime * 1000f;
            MelonLogger.Msg($"[StereoRender] frame {_heartbeatFrames}: {ms:F1} ms " +
                            $"({(ms > 0.01f ? 1000f / ms : 0f):F1} fps), submits {(_lastEndOfFrameSubmit >= 0 ? "end-of-frame" : "late-update")}");
        }

        /// <summary>
        /// Turns the eye cameras on or off as a pair.
        /// <para>
        /// They are switched off whenever the rig has no camera to follow, which is exactly the
        /// window a scene load sits in. Two extra HDRP cameras rendering at eye resolution, off
        /// settings cloned from a camera the loading screen has already destroyed, are pure cost on
        /// top of a frame that is already the heaviest one the game ever produces.
        /// </para>
        /// </summary>
        public void SetRenderingActive(bool active)
        {
            if (_renderingActive == active)
                return;

            _renderingActive = active;

            // Resuming after a pause: the coroutine gets its grace period again before the late
            // update fallback is allowed to step in and submit ahead of the render.
            if (active)
            {
                _heartbeatFrames = 0;
                _lastEndOfFrameSubmit = -1;
            }

            bool enable = active && VRConfig.EyeCamerasEnabled;
            if (LeftCam != null) LeftCam.enabled = enable;
            if (RightCam != null) RightCam.enabled = enable;

            MelonLogger.Msg($"[StereoRender] Eye cameras {(enable ? "resumed" : "paused")}");
        }

        /// <summary>
        /// Adds layers to what the eye cameras draw, without disturbing anything else about them.
        /// <para>
        /// Used when the game brings up a camera that exists to show one particular set of layers,
        /// the inventory being the case in point. Copying that camera wholesale would hand the eyes
        /// its three layer mask and its flat background colour, and the world would simply stop being
        /// drawn: the contents of the inventory would appear to float in an empty void. Merging its
        /// layers into the gameplay ones instead keeps the world and adds whatever it wanted shown.
        /// </para>
        /// </summary>
        public void AddCullingLayers(int extraMask)
        {
            if (LeftCam == null || RightCam == null)
                return;

            int merged = defaultCullingMask | extraMask;
            if (LeftCam.cullingMask == merged && RightCam.cullingMask == merged)
                return;

            LeftCam.cullingMask = merged;
            RightCam.cullingMask = merged;

            MelonLogger.Msg($"[StereoRender] Culling mask 0x{defaultCullingMask:X} + 0x{extraMask:X} " +
                            $"-> 0x{merged:X}");
        }

        private bool _loadingMode;

        /// <summary>
        /// Puts the eyes into darkness while a scene loads, without stopping them.
        /// <para>
        /// Nothing is drawn but the interface layers, and the background is cleared to black, so the
        /// player sees an empty room with the loading panel in it rather than a frozen frame of a
        /// world that is being taken apart around them.
        /// </para>
        /// </summary>
        public void SetLoadingMode(bool loading)
        {
            if (_loadingMode == loading || LeftCam == null || RightCam == null)
                return;

            _loadingMode = loading;

            if (loading)
            {
                LeftCam.clearFlags = CameraClearFlags.SolidColor;
                RightCam.clearFlags = CameraClearFlags.SolidColor;
                LeftCam.backgroundColor = Color.black;
                RightCam.backgroundColor = Color.black;

                // Interface layers only: the loading panel has to come through, the half built world
                // must not.
                int uiOnly = 1 << 5;
                LeftCam.cullingMask = uiOnly;
                RightCam.cullingMask = uiOnly;
            }
            else
            {
                LeftCam.clearFlags = CameraClearFlags.Skybox;
                RightCam.clearFlags = CameraClearFlags.Skybox;
                LeftCam.cullingMask = defaultCullingMask;
                RightCam.cullingMask = defaultCullingMask;
            }

            MelonLogger.Msg($"[StereoRender] Eyes {(loading ? "darkened for loading" : "back to the world")}");
        }

        /// <summary>Overrides the culling mask of both eye cameras (used to hide the VR view).</summary>
        public void SetCameraMask(int mask)
        {
            if (LeftCam != null) LeftCam.cullingMask = mask;
            if (RightCam != null) RightCam.cullingMask = mask;
        }

        /// <summary>Recreates the eye render targets if the recommended resolution changed.</summary>
        public void CheckResolution()
        {
            GetTargetSize(out int width, out int height);

            if (width == _currentWidth && height == _currentHeight)
                return;

            UpdateResolution();
        }

        private void GetTargetSize(out int width, out int height)
        {
            uint w = 0, h = 0;
            if (OpenVR.System != null)
                OpenVR.System.GetRecommendedRenderTargetSize(ref w, ref h);

            if (w == 0 || h == 0)
            {
                w = 2208;
                h = 2452;
            }

            float scale = Mathf.Clamp(renderScale, 0.25f, 2.0f);
            width = Mathf.Max(1, Mathf.RoundToInt(w * scale));
            height = Mathf.Max(1, Mathf.RoundToInt(h * scale));
        }

        /// <summary>
        /// Allocates the eye render targets at the recommended resolution of the runtime. The textures
        /// are created eagerly: <c>GetNativeTexturePtr</c> returns a null handle on a RenderTexture
        /// that has not been created yet, and the compositor rejects a null handle.
        /// </summary>
        public void UpdateResolution()
        {
            GetTargetSize(out _currentWidth, out _currentHeight);

            ReleaseTargets();

            LeftRT = CreateEyeTarget("VRLeftEyeRT");
            RightRT = CreateEyeTarget("VRRightEyeRT");

            ApplyRenderTargets();

            uint rw = 0, rh = 0;
            if (OpenVR.System != null)
                OpenVR.System.GetRecommendedRenderTargetSize(ref rw, ref rh);

            float megaPixelsPerEye = _currentWidth * _currentHeight / 1000000f;
            MelonLogger.Msg($"[StereoRender] Eye targets: {_currentWidth}x{_currentHeight} " +
                            $"({megaPixelsPerEye:F1} MPix/eye, {megaPixelsPerEye * 2f:F1} MPix total) " +
                            $"[runtime recommends {rw}x{rh}, renderScale {renderScale:F2}, msaa {msaaSamples}]");
        }

        private RenderTexture CreateEyeTarget(string targetName)
        {
            // OpenVR Auto assumes gamma-encoded pixels for these 8-bit targets; it does not
            // infer linear data from Unity's color space. Store sRGB pixels so that assumption
            // is correct, including through Proton/xrizer. Unity decodes them when sampling
            // the desktop mirror, keeping both output paths consistent.
            var rt = new RenderTexture(_currentWidth, _currentHeight, 24, RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.sRGB)
            {
                name = targetName,
                antiAliasing = Mathf.Max(1, msaaSamples),
                useMipMap = false,
                autoGenerateMips = false,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            rt.Create();

            MelonLogger.Msg($"[StereoRender] {targetName}: format {rt.graphicsFormat}, " +
                            $"sRGB {rt.sRGB}, game color space {QualitySettings.activeColorSpace}, " +
                            $"compositor color space {EColorSpace.Auto}");

            // Cleared once, so the border left outside a narrowed viewport is black rather than
            // whatever happened to be in memory. The cameras only ever draw inside their viewport,
            // so this is the only thing that ever writes those pixels.
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(true, true, Color.black);
            RenderTexture.active = previous;

            return rt;
        }

        private void ApplyRenderTargets()
        {
            if (LeftCam != null)
                LeftCam.targetTexture = LeftRT;

            if (RightCam != null)
                RightCam.targetTexture = RightRT;

            ApplyProjections();
        }

        private void ReleaseTargets()
        {
            if (LeftCam != null) LeftCam.targetTexture = null;
            if (RightCam != null) RightCam.targetTexture = null;

            if (LeftRT != null)
            {
                LeftRT.Release();
                Destroy(LeftRT);
                LeftRT = null;
            }

            if (RightRT != null)
            {
                RightRT.Release();
                Destroy(RightRT);
                RightRT = null;
            }
        }

        /// <summary>
        /// Submits the eye targets once every camera of the frame has finished rendering.
        /// </summary>
        private IEnumerator SubmitAtEndOfFrame()
        {
            var endOfFrame = new WaitForEndOfFrame();
            int lastFrame = -1;
            int sameFrameResumes = 0;

            while (true)
            {
                yield return endOfFrame;

                int frame = Time.frameCount;
                if (frame == lastFrame)
                {
                    // Resuming inside the frame we just yielded in means the coroutine runner is not
                    // honouring WaitForEndOfFrame. With an unbounded loop that spins forever and
                    // hangs the game, so give up and leave the submitting to the late update path.
                    if (++sameFrameResumes > 4)
                    {
                        MelonLogger.Warning("[StereoRender] WaitForEndOfFrame is not honoured here, " +
                                            "submitting from late update instead");
                        yield break;
                    }
                }
                else
                {
                    sameFrameResumes = 0;
                    lastFrame = frame;
                }

                if (stereoRenderPass != null && stereoRenderPass.Execute())
                {
                    _lastEndOfFrameSubmit = frame;
                    CaptureMirrorView();
                }
            }
        }

        /// <summary>
        /// Submits from the late update phase, but only while the end-of-frame coroutine is not
        /// delivering. Keeps the headset alive if the coroutine runner of MelonLoader cannot service
        /// <c>WaitForEndOfFrame</c> in this game, at the cost of the old one-frame latency.
        /// </summary>
        public void SubmitFallbackIfNeeded()
        {
            if (stereoRenderPass == null)
                return;

            // While the rig is standing down nothing submits by design, so the staleness check below
            // would otherwise read as a broken coroutine and warn about it every scene load.
            if (!_renderingActive)
                return;

            // Never submit from here until the end-of-frame coroutine has had a fair chance to run.
            // This phase happens before the eye cameras have rendered, so it hands the compositor a
            // texture the render thread may still be writing into. That race is what used to hang the
            // game on its very first frame, the one frame the coroutine had not yet covered.
            if (_heartbeatFrames < FallbackGraceFrames)
                return;

            if (Time.frameCount - _lastEndOfFrameSubmit <= 2)
                return;

            if (!_fallbackWarned)
            {
                _fallbackWarned = true;
                MelonLogger.Warning("[StereoRender] End-of-frame submit is not running, " +
                                    "falling back to late update (one frame of extra latency)");
            }

            stereoRenderPass.Execute();
        }

        /// <summary>
        /// Hands the two eye textures to the OpenVR compositor.
        /// </summary>
        public class StereoRenderPass
        {
            private readonly StereoRender _stereoRender;
            private int _lastSubmittedFrame = -1;
            private int _errorLogCooldown;

            private static readonly System.Diagnostics.Stopwatch SubmitTimer =
                new System.Diagnostics.Stopwatch();

            private static int _submitStalls;
            private static double _worstSubmitMs;
            private static int _submitReportCountdown;

            /// <summary>Starts timing the call about to be made.</summary>
            private static void SubmitClock()
            {
                if (VRConfig.SubmitStallMs <= 0f)
                    return;

                SubmitTimer.Restart();
            }

            /// <summary>
            /// Notes how long a call into the compositor took, and says so when it took too long.
            /// <para>
            /// Measured because the main thread has twice stopped inside Submit with nothing in any log
            /// to say why. If these calls are stalling routinely then the fault is on this side, in the
            /// eye textures being handed over while the render thread is still working on them; if they
            /// run clean for an hour and then one never returns, it is the runtime underneath.
            /// </para>
            /// </summary>
            private static void RecordSubmit(string which)
            {
                if (VRConfig.SubmitStallMs <= 0f)
                    return;

                SubmitTimer.Stop();
                double ms = SubmitTimer.Elapsed.TotalMilliseconds;

                if (ms > _worstSubmitMs)
                    _worstSubmitMs = ms;

                if (ms >= VRConfig.SubmitStallMs)
                {
                    _submitStalls++;
                    MelonLogger.Warning($"[StereoRender] {which} took {ms:F0} ms " +
                                        $"(frame {Time.frameCount}, {_submitStalls} stalls so far)");
                }

                // A periodic line even when nothing stalls, so a clean run leaves proof it was clean
                // rather than leaving an empty log to be read as the measurement never having run.
                if (--_submitReportCountdown > 0)
                    return;

                _submitReportCountdown = 3000;

                MelonLogger.Msg($"[StereoRender] compositor calls: {_submitStalls} over " +
                                $"{VRConfig.SubmitStallMs:F0} ms, worst {_worstSubmitMs:F1} ms");

                _worstSubmitMs = 0.0;
            }
            private int _traceCount;

            public bool isRendering;

            public StereoRenderPass(StereoRender stereoRender)
            {
                _stereoRender = stereoRender;
            }

            /// <summary>
            /// Submits both eyes. Returns true when a submission actually happened, which is how the
            /// end-of-frame coroutine proves to <see cref="SubmitFallbackIfNeeded"/> that it is alive.
            /// Guarded per frame so the coroutine and the fallback can never double-submit.
            /// </summary>
            public bool Execute()
            {
                if (_stereoRender == null || !_stereoRender.enabled)
                    return false;

                // Nothing is rendering into the eye targets while the rig is standing down, and
                // WaitGetPoses is being skipped, so submitting here would break the pairing the
                // compositor expects.
                if (!_stereoRender.RenderingActive)
                    return false;

                if (OpenVR.Compositor == null || SteamVR.instance == null)
                    return false;

                RenderTexture left = _stereoRender.LeftRT;
                RenderTexture right = _stereoRender.RightRT;

                if (left == null || right == null || !left.IsCreated() || !right.IsCreated())
                    return false;

                if (_lastSubmittedFrame == Time.frameCount)
                    return true;

                if (!VRConfig.SubmitToCompositor)
                    return false;

                _lastSubmittedFrame = Time.frameCount;
                isRendering = true;

                // Submit hands a native texture pointer to the compositor. If that call blocks, the
                // game hangs with nothing in the log, so the first frames are traced around it.
                bool trace = _traceCount < VRConfig.TraceFrames;
                if (trace)
                {
                    _traceCount++;
                    MelonLogger.Msg($"[StereoRender] Submit enter (frame {_lastSubmittedFrame}, " +
                                    $"type {SteamVR.instance.textureType})");
                }

                var leftTex = new Texture_t
                {
                    handle = left.GetNativeTexturePtr(),
                    eType = SteamVR.instance.textureType,
                    eColorSpace = EColorSpace.Auto
                };

                var rightTex = new Texture_t
                {
                    handle = right.GetNativeTexturePtr(),
                    eType = SteamVR.instance.textureType,
                    eColorSpace = EColorSpace.Auto
                };

                if (leftTex.handle == IntPtr.Zero || rightTex.handle == IntPtr.Zero)
                {
                    isRendering = false;
                    return false;
                }

                // The texture origin of Unity is top-left, OpenVR expects bottom-left, hence the V flip.
                var bounds = new VRTextureBounds_t { uMin = 0, vMin = 1, uMax = 1, vMax = 0 };

                VRProfiler.Begin();

                VRWatchdog.Stage(VRWatchdog.SubmittingLeft);
                SubmitClock();
                EVRCompositorError errorL = OpenVR.Compositor.Submit(EVREye.Eye_Left, ref leftTex, ref bounds, EVRSubmitFlags.Submit_Default);
                RecordSubmit("left");
                if (trace)
                    MelonLogger.Msg($"[StereoRender] Submit left done -> {errorL}");

                VRWatchdog.Stage(VRWatchdog.SubmittingRight);
                SubmitClock();
                EVRCompositorError errorR = OpenVR.Compositor.Submit(EVREye.Eye_Right, ref rightTex, ref bounds, EVRSubmitFlags.Submit_Default);
                RecordSubmit("right");
                if (trace)
                    MelonLogger.Msg($"[StereoRender] Submit right done -> {errorR}");

                // Tell the compositor the frame is handed over and it may get on with it.
                //
                // Without this the compositor holds the next WaitGetPoses until it judges our frame
                // finished, while we sit inside that call waiting for it: the watchdog caught the
                // main thread parked in exactly that spot, with the headset showing "waiting".
                // Handing off explicitly ends the frame on our terms rather than leaving the two
                // sides waiting on each other.
                if (VRConfig.PostPresentHandoff)
                {
                    VRWatchdog.Stage(VRWatchdog.HandingOff);
                    SubmitClock();
                    OpenVR.Compositor.PostPresentHandoff();
                    RecordSubmit("handoff");
                }

                VRWatchdog.Stage(VRWatchdog.Idle);

                VRProfiler.End(VRProfiler.Section.Submit);

                if (errorL != EVRCompositorError.None || errorR != EVRCompositorError.None)
                {
                    if (--_errorLogCooldown <= 0)
                    {
                        _errorLogCooldown = 300;
                        MelonLogger.Warning($"[StereoRender] Submit failed (L={errorL}, R={errorR})");
                    }
                }

                isRendering = false;
                return true;
            }
        }
    }
}
