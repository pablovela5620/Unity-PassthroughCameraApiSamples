// Copyright (c) Meta Platforms, Inc. and affiliates.
// Combined and simplified from CameraToWorldManager (PassthroughCameraApiSamples)
// and ARFlowMetaQuest (gRPC upload sample) into a single component that:
//   * Manages the Meta Quest passthrough WebCamTexture stream
//   * Registers the camera with an ARFlow server via gRPC
//   * Sends a single RGB frame every time the user takes a snapshot (Button A)
//   * Maintains the original canvas‑in‑world visualisation & debug helpers
//
// ────────────────────────────────────────────────────────────────────────────────
// Requirements
//   • Meta Quest project set‑up with the Passthrough Camera API samples
//   • ARFlow C# client (+ Google.Protobuf) available in the project
//   • TMP & UI packages for the optional address input (can be removed)
// ────────────────────────────────────────────────────────────────────────────────
using System;
using System.Collections;
using ARFlow;
using Google.Protobuf;
using Meta.XR.Samples;
using PassthroughCameraSamples;
using PassthroughCameraSamples.CameraToWorld; // For utility + canvas types
using TMPro;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.UI;

public class ARFlowMetaQuest : MonoBehaviour
{
    // ────────────────────────── Passthrough / Visualisation ─────────────────────
    [Header("Passthrough")]
    [SerializeField] private WebCamTextureManager _webCamTextureManager;
    private PassthroughCameraEye Eye => _webCamTextureManager.Eye;
    private Vector2Int CameraResolution => _webCamTextureManager.RequestedResolution;

    [SerializeField] private GameObject _centerEyeAnchor;
    [SerializeField] private CameraToWorldCameraCanvas _cameraCanvas;
    [SerializeField] private float _canvasDistance = 1f;

    // Optional debug helpers (can be removed)
    [SerializeField] private GameObject _headMarker;
    [SerializeField] private GameObject _cameraMarker;
    [SerializeField] private GameObject _rayMarker;
    [SerializeField] private Vector3 _headSpaceDebugShift = new(0f, -0.15f, 0.4f);

    private GameObject _rayGo1, _rayGo2, _rayGo3, _rayGo4;

    // Snapshot state
    private bool _snapshotTaken;
    private OVRPose _snapshotHeadPose;
    private bool _debugOn;

    // ─────────────────────────────── gRPC ───────────────────────────────────────
    [Header("ARFlow gRPC")][Tooltip("Leave blank to auto‑connect to hard‑coded address")] public TMP_InputField addressInput;
    public Button connectButton;

    [Tooltip("Server address used if no UI field present")][SerializeField] private string _defaultServerAddress = "http://100.114.111.55:8500";

    private ARFlowClient _client;
    private bool _connected;

    // Local buffer used to copy WebCamTexture pixels before sending
    private Texture2D _snapshotTexture;

    // ───────────────────────────── Unity lifecycle ──────────────────────────────
    private void Awake() => OVRManager.display.RecenteredPose += OnRecentered;

    private IEnumerator Start()
    {
        if (_webCamTextureManager == null)
        {
            Debug.LogError("ARFlowMetaQuest: WebCamTextureManager reference missing");
            enabled = false;
            yield break;
        }

        // Wait until camera permission granted before enabling WebCamTextureManager
        Assert.IsFalse(_webCamTextureManager.enabled);
        while ((bool)!PassthroughCameraPermissions.HasCameraPermission)
        {
            yield return null;
        }

        // Use native resolution reported by the camera intrinsics
        _webCamTextureManager.RequestedResolution = PassthroughCameraUtils.GetCameraIntrinsics(Eye).Resolution;
        _webCamTextureManager.enabled = true;

        // Build canvas size to exactly match camera FOV
        ScaleCameraCanvas();

        // Debug rays (optional)
        _rayGo1 = _rayMarker;
        _rayGo2 = Instantiate(_rayMarker);
        _rayGo3 = Instantiate(_rayMarker);
        _rayGo4 = Instantiate(_rayMarker);
        UpdateRaysRendering();

        // Hook up UI (optional)
        if (connectButton) connectButton.onClick.AddListener(ConnectToServer);
        else // auto‑connect when no UI present
        {
            ConnectToServer();
        }
    }

    private void Update()
    {
        if (_webCamTextureManager.WebCamTexture == null) return;

        // ── Take / drop snapshot (A button) ─────────────────────────────────–––
        if (OVRInput.GetDown(OVRInput.Button.One))
        {
            _snapshotTaken = !_snapshotTaken;

            if (_snapshotTaken)
            {
                TakeSnapshotAndSend();
            }
            else
            {
                ResumeStreaming();
            }
            UpdateRaysRendering();
        }

        // ── Toggle debug (B button) ─────────────────────────────────────────–––
        if (OVRInput.GetDown(OVRInput.Button.Two))
        {
            _debugOn ^= true;
            UpdateRaysRendering();
            if (_snapshotTaken) TranslateMarkersForDebug(_debugOn);
        }

        // ── Marker updates (only when live video is playing) ─────────────────––
        if (!_snapshotTaken) UpdateMarkerPoses();
    }

    // ───────────────────────────── Snapshot logic ──────────────────────────────
    private void TakeSnapshotAndSend()
    {
        // Ask canvas to display snapshot
        _cameraCanvas.MakeCameraSnapshot();

        // Capture last frame from WebCamTexture
        var wct = _webCamTextureManager.WebCamTexture;
        _snapshotTexture ??= new Texture2D(wct.width, wct.height, TextureFormat.RGB24, false);
        _snapshotTexture.SetPixels32(wct.GetPixels32());
        _snapshotTexture.Apply(false, false);
        byte[] pixelBytes = _snapshotTexture.GetRawTextureData();

        if (_connected)
        {
            _client.SendFrame(new DataFrameRequest { Color = ByteString.CopyFrom(pixelBytes) });
        }
        else
        {
            Debug.LogWarning("ARFlowMetaQuest: Snapshot captured but no active gRPC connection");
        }

        // Freeze camera feed (matches original sample behaviour)
        wct.Stop();
        _snapshotHeadPose = _centerEyeAnchor.transform.ToOVRPose();
    }

    private void ResumeStreaming()
    {
        _webCamTextureManager.WebCamTexture.Play();
        _cameraCanvas.ResumeStreamingFromCamera();
        _snapshotHeadPose = OVRPose.identity;
    }

    // ───────────────────────────── gRPC helpers ────────────────────────────────
    private void ConnectToServer()
    {
        if (_connected) return;
        string serverAddress = addressInput ? addressInput.text : _defaultServerAddress;
        if (string.IsNullOrWhiteSpace(serverAddress)) serverAddress = _defaultServerAddress;

        try
        {
            _client = new ARFlowClient(serverAddress);
            RegisterWithServer();
            _connected = true;
            Debug.Log($"ARFlowMetaQuest: Connected to {serverAddress}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"ARFlowMetaQuest: Failed to connect → {ex.Message}");
        }
    }

    private void RegisterWithServer()
    {
        var intr = PassthroughCameraUtils.GetCameraIntrinsics(Eye);
        _client.Connect(new RegisterRequest
        {
            DeviceName = "MetaQuestPassthrough",
            CameraIntrinsics = new RegisterRequest.Types.CameraIntrinsics
            {
                FocalLengthX = intr.FocalLength.x,
                FocalLengthY = intr.FocalLength.y,
                ResolutionX = intr.Resolution.x,
                ResolutionY = intr.Resolution.y,
                PrincipalPointX = intr.PrincipalPoint.x,
                PrincipalPointY = intr.PrincipalPoint.y
            },
            CameraColor = new RegisterRequest.Types.CameraColor
            {
                Enabled = true,
                DataType = "RGB24",
                ResizeFactorX = 1.0f,
                ResizeFactorY = 1.0f
            },
            CameraDepth = new RegisterRequest.Types.CameraDepth { Enabled = false },
            CameraTransform = new RegisterRequest.Types.CameraTransform { Enabled = false }
        });
    }

    // ───────────────────────────── Visual helpers ──────────────────────────────
    private void ScaleCameraCanvas()
    {
        var canvasRect = _cameraCanvas.GetComponentInChildren<RectTransform>();
        var leftRay = PassthroughCameraUtils.ScreenPointToRayInCamera(Eye, new Vector2Int(0, CameraResolution.y / 2));
        var rightRay = PassthroughCameraUtils.ScreenPointToRayInCamera(Eye, new Vector2Int(CameraResolution.x, CameraResolution.y / 2));
        var hfovDeg = Vector3.Angle(leftRay.direction, rightRay.direction);
        var canvasWidth = 2 * _canvasDistance * Mathf.Tan(hfovDeg * Mathf.Deg2Rad / 2f);
        var scale = (float)(canvasWidth / canvasRect.sizeDelta.x);
        canvasRect.localScale = Vector3.one * scale;
    }

    private void UpdateMarkerPoses()
    {
        // Move markers to current poses
        var headPose = OVRPlugin.GetNodePoseStateImmediate(OVRPlugin.Node.Head).Pose.ToOVRPose();
        _headMarker.transform.SetPositionAndRotation(headPose.position, headPose.orientation);

        var camPose = PassthroughCameraUtils.GetCameraPoseInWorld(Eye);
        _cameraMarker.transform.SetPositionAndRotation(camPose.position, camPose.rotation);

        // Move canvas in front of camera
        _cameraCanvas.transform.SetPositionAndRotation(camPose.position + camPose.rotation * Vector3.forward * _canvasDistance, camPose.rotation);

        // Rays to 4 image corners
        var rays = new[]
        {
            new { go = _rayGo1, uv = new Vector2Int(0, 0) },
            new { go = _rayGo2, uv = new Vector2Int(0, CameraResolution.y) },
            new { go = _rayGo3, uv = new Vector2Int(CameraResolution.x, CameraResolution.y) },
            new { go = _rayGo4, uv = new Vector2Int(CameraResolution.x, 0) }
        };
        foreach (var r in rays)
        {
            var rayW = PassthroughCameraUtils.ScreenPointToRayInWorld(Eye, r.uv);
            r.go.transform.position = rayW.origin;
            r.go.transform.LookAt(rayW.origin + rayW.direction);
            float angle = Vector3.Angle(r.go.transform.forward, camPose.rotation * Vector3.forward);
            float zScale = (float)(_canvasDistance / Math.Cos(angle * Mathf.Deg2Rad) / 0.5f); // original ray length 0.5
            var s = r.go.transform.localScale;
            r.go.transform.localScale = new Vector3(s.x, s.y, zScale);
            r.go.GetComponentInChildren<Text>().text = $"({r.uv.x}, {r.uv.y})";
        }
    }

    private void UpdateRaysRendering()
    {
        foreach (var go in new[] { _rayGo1, _rayGo2, _rayGo3, _rayGo4 })
        {
            go.GetComponent<CameraToWorldRayRenderer>().RenderMiddleSegment(_snapshotTaken || _debugOn);
        }
    }

    private void TranslateMarkersForDebug(bool moveForward)
    {
        var objs = new[] { _headMarker, _cameraMarker, _cameraCanvas.gameObject, _rayGo1, _rayGo2, _rayGo3, _rayGo4 };
        var dir = _snapshotTaken ? _snapshotHeadPose.orientation : _centerEyeAnchor.transform.rotation;
        foreach (var o in objs) o.transform.position += dir * _headSpaceDebugShift * (moveForward ? 1 : -1);
    }

    private void OnRecentered()
    {
        if (!_snapshotTaken) return;
        _snapshotTaken = false;
        ResumeStreaming();
    }
}
