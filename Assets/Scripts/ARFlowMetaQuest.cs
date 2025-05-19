// Copyright (c) Meta Platforms, Inc. and affiliates.
//
// Quest-3 passthrough (or Main Camera in-editor) → ARFlow RGB uploader.

using System.Collections;
using Meta.XR.Samples;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Google.Protobuf;
using ARFlow;
using Unity.Profiling;

namespace PassthroughCameraSamples.ARFlowBridge
{
    [MetaCodeSample("PassthroughCameraApiSamples-CameraViewer+ARFlow")]
    public class ARFlowMetaQuest : MonoBehaviour
    {
        // ─── Required for on-device streaming ────────────────────────────────
        [Header("Passthrough webcam (Quest)")]
        [SerializeField] private WebCamTextureManager m_webCamTextureManager;

        // ─── Optional preview/debug UI ───────────────────────────────────────
        [Header("Preview / Debug (optional)")]
        [SerializeField] private RawImage m_previewImage;
        [SerializeField] private Text     m_debugText;

        // ─── ARFlow settings ─────────────────────────────────────────────────
        [Header("ARFlow connection")]
        [Tooltip("gRPC endpoint, e.g. 192.168.0.2:8500")]
        [SerializeField] private string   m_serverAddress = "100.110.132.55:8500";
        [Tooltip("Device name shown on the ARFlow dashboard")]
        [SerializeField] private string   m_deviceName    = "Quest3_Passthrough";

        // ─── Internal state ─────────────────────────────────────────────────
        private ARFlowClient _client;
        private Texture2D    _cpuTex;
        private Vector2Int   _res;

        private bool   _usingMainCamera;   // true only when running in editor
        private Camera _mainCam;
        private RenderTexture _mainRT;

        private bool   _streaming;

        private static readonly ProfilerMarker CopyMarker =
            new ProfilerMarker("CopyRGBtoCPU");

        // ─── Life-cycle ─────────────────────────────────────────────────────
        private void Start() => StartCoroutine(Init());

        private IEnumerator Init()
        {
#if UNITY_EDITOR
            // In the editor we default to Main Camera for debugging.
            if (m_webCamTextureManager == null ||
                m_webCamTextureManager.WebCamTexture == null)
            {
                _usingMainCamera = true;
                _mainCam = Camera.main;
                if (_mainCam == null)
                {
                    Debug.LogError("[ARFlowMetaQuest] No Main Camera found for editor debug.");
                    yield break;
                }

                _res = new Vector2Int(_mainCam.pixelWidth, _mainCam.pixelHeight);
                _cpuTex = new Texture2D(_res.x, _res.y, TextureFormat.RGB24, false);
                _mainRT = new RenderTexture(_res.x, _res.y, 24, RenderTextureFormat.ARGB32);
                _mainCam.targetTexture = _mainRT;
                if (m_previewImage) m_previewImage.texture = _mainRT;

                Log($"Editor debug: streaming Main Camera {_res.x}×{_res.y}");
                ConnectAndStart();
                yield break;      // skip passthrough init
            }
#endif
            // ── Device path: wait for the passthrough stream ─────────────────
            while (m_webCamTextureManager.WebCamTexture == null ||
                   m_webCamTextureManager.WebCamTexture.width <= 16)
            {
                yield return null;
            }

            var camTex = m_webCamTextureManager.WebCamTexture;
            _res = new Vector2Int(camTex.width, camTex.height);
            _cpuTex = new Texture2D(_res.x, _res.y, TextureFormat.RGB24, false);

            if (m_previewImage) m_previewImage.texture = camTex;
            Log($"WebCam ready {_res.x}×{_res.y}");

            ConnectAndStart();
        }

        // ─── Connect to ARFlow & begin streaming ────────────────────────────
        private void ConnectAndStart()
        {
            _client = new ARFlowClient($"http://{m_serverAddress}");

            // Rough intrinsics
            float fx = 0, fy = 0;
            if (_usingMainCamera && _mainCam)
            {
                Matrix4x4 P = _mainCam.projectionMatrix;
                fx = P[0,0] * _res.x;
                fy = P[1,1] * _res.y;
            }
            else
            {
                var eye = m_webCamTextureManager?.Eye;
                if (eye != null)
                {
                    fx = 0.5f * _res.x / Mathf.Tan(99 * 0.5f * Mathf.Deg2Rad);
                    fy = 0.5f * _res.y / Mathf.Tan(99 * 0.5f * Mathf.Deg2Rad);
                }
            }

            _client.Connect(new RegisterRequest
            {
                DeviceName = m_deviceName,
                CameraIntrinsics = new RegisterRequest.Types.CameraIntrinsics
                {
                    FocalLengthX    = fx,
                    FocalLengthY    = fy,
                    ResolutionX     = _res.x,
                    ResolutionY     = _res.y,
                    PrincipalPointX = _res.x * 0.5f,
                    PrincipalPointY = _res.y * 0.5f
                },
                CameraColor = new RegisterRequest.Types.CameraColor
                {
                    Enabled       = true,
                    DataType      = "RGB24",
                    ResizeFactorX = 1.0f,
                    ResizeFactorY = 1.0f
                },
                CameraDepth     = new RegisterRequest.Types.CameraDepth     { Enabled = false },
                CameraTransform = new RegisterRequest.Types.CameraTransform { Enabled = false }
            });

            Log($"Connected to {m_serverAddress}");
            _streaming = true;
        }

        // ─── Per-frame upload ───────────────────────────────────────────────
        private void Update()
        {
            if (!_streaming || _client == null) return;
            UploadFrame();
        }

        private void UploadFrame()
        {
            if (_usingMainCamera)
            {
                _mainCam.Render();
                ReadRTToTexture(_mainRT);
            }
            else
            {
                var camTex = m_webCamTextureManager.WebCamTexture;
                if (!camTex.didUpdateThisFrame) return;
                CopyWebCamToTexture(camTex);
            }

            _client.SendFrame(new DataFrameRequest
            {
                Color = ByteString.CopyFrom(_cpuTex.GetRawTextureData())
            });
        }

        // ─── Helpers ─────────────────────────────────────────────────────────
        private void CopyWebCamToTexture(WebCamTexture camTex)
        {
            CopyMarker.Begin();
            var rt = RenderTexture.GetTemporary(camTex.width, camTex.height, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(camTex, rt);
            ReadRTToTexture(rt);
            RenderTexture.ReleaseTemporary(rt);
            CopyMarker.End();
        }

        private void ReadRTToTexture(RenderTexture rt)
        {
            RenderTexture.active = rt;
            _cpuTex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            _cpuTex.Apply(false);
            RenderTexture.active = null;
        }

        private void OnDestroy()
        {
#if UNITY_EDITOR
            if (_usingMainCamera && _mainCam) _mainCam.targetTexture = null;
            if (_mainRT) _mainRT.Release();
#endif
            //_client?.Dispose();
        }

        private void Log(string msg)
        {
            Debug.Log(msg);
            if (m_debugText) m_debugText.text = msg;
        }
    }
}
