// Copyright (c) Meta Platforms, Inc. and affiliates.
//
// Quest-3 passthrough (or Main Camera in-editor) → ARFlow RGB uploader.

using System.Collections;
using System.Threading.Tasks;
using Meta.XR.Samples;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Rendering;
using TMPro;
using Google.Protobuf;
using ARFlow;
using Unity.Profiling;

namespace PassthroughCameraSamples.ARFlowBridge
{
    [MetaCodeSample("PassthroughCameraApiSamples-CameraViewer+ARFlow")]
    public class ARFlowMetaQuest : MonoBehaviour
    {
        [Header("Passthrough webcam (Quest)")]
        [SerializeField] private WebCamTextureManager m_webCamTextureManager;

        [Header("Preview / Debug (optional)")]
        [SerializeField] private RawImage m_previewImage;
        [SerializeField] private Text     m_debugText;

        [Header("ARFlow connection")]
        [Tooltip("gRPC endpoint, e.g. 192.168.0.2:8500")]
        [SerializeField] private string   m_serverAddress = "100.110.132.55:8500";
        [Tooltip("Device name shown on the ARFlow dashboard")]
        [SerializeField] private string   m_deviceName    = "Quest3_Passthrough";
        [Tooltip("Text element to display FPS and latency")]
        [SerializeField] private TextMeshProUGUI m_statsText;
        [Tooltip("Factor to scale down the frame resolution before sending (0.1 to 1.0)")]
        [SerializeField, Range(0.1f, 1.0f)] private float m_renderScale = 0.5f;

        private ARFlowClient _client;
        private Texture2D    _cpuTex;
        private Vector2Int   _res;

        private bool   _usingMainCamera;
        private Camera _mainCam;
        private RenderTexture _mainRT;

        private bool   _streaming;
        private float  _latencyMs;
        private int    _framesSentInInterval;
        private float  _intervalTimer;
        private float  _uploadFps;
        private bool   _isProcessingFrame;

        private static readonly ProfilerMarker CopyMarker = new ProfilerMarker("CopyRGBtoCPU");
        private TaskCompletionSource<bool> _gpuReadbackTCS;

        private void Start() => StartCoroutine(Init());

        private IEnumerator Init()
        {
#if UNITY_EDITOR
            if (m_webCamTextureManager == null || m_webCamTextureManager.WebCamTexture == null)
            {
                _usingMainCamera = true;
                _mainCam = Camera.main;
                if (_mainCam == null)
                {
                    Debug.LogError("[ARFlowMetaQuest] No Main Camera found for editor debug.");
                    yield break;
                }

                _res = new Vector2Int(_mainCam.pixelWidth, _mainCam.pixelHeight);
                _mainRT = new RenderTexture(_res.x, _res.y, 24, RenderTextureFormat.ARGB32);
                _mainCam.targetTexture = _mainRT;
                if (m_previewImage) m_previewImage.texture = _mainRT;

                Log($"Editor debug: streaming Main Camera {_res.x}×{_res.y}");
                ConnectAndStart();
                yield break;
            }
#endif
            while (m_webCamTextureManager.WebCamTexture == null ||
                   m_webCamTextureManager.WebCamTexture.width <= 16)
            {
                yield return null;
            }

            var camTex = m_webCamTextureManager.WebCamTexture;
            _res = new Vector2Int(camTex.width, camTex.height);

            if (m_previewImage) m_previewImage.texture = camTex;
            Log($"WebCam ready {_res.x}×{_res.y}");

            ConnectAndStart();
        }

        private void ConnectAndStart()
        {
            _client = new ARFlowClient($"http://{m_serverAddress}");

            float originalFx = 0, originalFy = 0;
            float principalPointX = 0, principalPointY = 0;
            int cameraResolutionX = _res.x;
            int cameraResolutionY = _res.y;

            if (_usingMainCamera && _mainCam)
            {
                Matrix4x4 P = _mainCam.projectionMatrix;
                originalFx = P[0,0] * cameraResolutionX;
                originalFy = P[1,1] * cameraResolutionY;
                principalPointX = cameraResolutionX * 0.5f;
                principalPointY = cameraResolutionY * 0.5f;
            }
            else
            {
                var eyeToUse = PassthroughCameraEye.Left; 
                if (m_webCamTextureManager != null) {
                    if (System.Enum.IsDefined(typeof(PassthroughCameraEye), m_webCamTextureManager.Eye.ToString()))
                    {
                        eyeToUse = (PassthroughCameraEye)System.Enum.Parse(typeof(PassthroughCameraEye), m_webCamTextureManager.Eye.ToString());
                    }
                    else
                    {
                        Debug.LogWarning($"[ARFlowMetaQuest] m_webCamTextureManager.Eye ('{m_webCamTextureManager.Eye}') is not a valid PassthroughCameraEye. Defaulting to Left.");
                    }
                }

                PassthroughCameraIntrinsics intrinsics = PassthroughCameraUtils.GetCameraIntrinsics(eyeToUse);

                if (intrinsics.Resolution.x > 0 && intrinsics.Resolution.y > 0)
                {
                    originalFx = intrinsics.FocalLength.x;
                    originalFy = intrinsics.FocalLength.y;
                    principalPointX = intrinsics.PrincipalPoint.x;
                    principalPointY = intrinsics.PrincipalPoint.y;
                    cameraResolutionX = intrinsics.Resolution.x;
                    cameraResolutionY = intrinsics.Resolution.y;
                    _res = new Vector2Int(cameraResolutionX, cameraResolutionY);
                }
                else
                {
                    Debug.LogError("[ARFlowMetaQuest] Failed to get valid camera intrinsics from PassthroughCameraUtils.");
                }
            }

            Debug.Log($"[ARFlowMetaQuest] Camera Intrinsics: fx={originalFx:F2}, fy={originalFy:F2}, " +
                      $"cx={principalPointX:F2}, cy={principalPointY:F2}, " + 
                      $"resolution={cameraResolutionX}x{cameraResolutionY}");

            int scaledWidth = Mathf.Max(1, Mathf.RoundToInt(cameraResolutionX * m_renderScale));
            int scaledHeight = Mathf.Max(1, Mathf.RoundToInt(cameraResolutionY * m_renderScale));

            _client.Connect(new RegisterRequest
            {
                DeviceName = m_deviceName,
                CameraIntrinsics = new RegisterRequest.Types.CameraIntrinsics
                {
                    FocalLengthX    = originalFx * m_renderScale,
                    FocalLengthY    = originalFy * m_renderScale,
                    ResolutionX     = scaledWidth,
                    ResolutionY     = scaledHeight,
                    PrincipalPointX = principalPointX * m_renderScale,
                    PrincipalPointY = principalPointY * m_renderScale
                },
                CameraColor = new RegisterRequest.Types.CameraColor
                {
                    Enabled       = true,
                    DataType      = "RGB24",
                    ResizeFactorX = 1.0f,
                    ResizeFactorY = 1.0f
                },
                CameraDepth     = new RegisterRequest.Types.CameraDepth     { Enabled = false },
                CameraTransform = new RegisterRequest.Types.CameraTransform { Enabled = true }
            });

            Log($"Connected to {m_serverAddress}");
            _streaming = true;
        }

        private void Update()
        {
            if (!_streaming || _client == null || _isProcessingFrame) return;
            _ = UploadFrameAsync();

            _intervalTimer += Time.unscaledDeltaTime;
            if (_intervalTimer >= 1.0f)
            {
                _uploadFps = _framesSentInInterval / _intervalTimer;
                Debug.Log($"[ARFlowMetaQuest] Client Sending FPS: {_uploadFps:F1}");
                _framesSentInInterval = 0;
                _intervalTimer -= 1.0f;
            }

            if (m_statsText != null)
            {
                m_statsText.text = $"FPS: {_uploadFps:F1} | Latency: {_latencyMs:F1} ms";
            }
        }

        private async Task UploadFrameAsync()
        {
            if (_isProcessingFrame) return;
            _isProcessingFrame = true;

            try
            {
                Pose cameraPose = new Pose();

                if (_usingMainCamera)
                {
                    if (_mainCam == null || _mainRT == null) 
                    {
                        _isProcessingFrame = false; return;
                    }
                    _mainCam.Render();
                    await ReadRTToTextureAsync(_mainRT);
                    cameraPose.position = _mainCam.transform.position;
                    cameraPose.rotation = _mainCam.transform.rotation;
                }
                else
                {
                    var camTex = m_webCamTextureManager?.WebCamTexture;
                    if (camTex == null || !camTex.didUpdateThisFrame)
                    {
                        _isProcessingFrame = false;
                        return;
                    }
                    await CopyWebCamToTextureAsync(camTex);

                    var eyeToUse = PassthroughCameraEye.Left; 
                    if (m_webCamTextureManager != null)
                    {
                        if (System.Enum.IsDefined(typeof(PassthroughCameraEye), m_webCamTextureManager.Eye.ToString()))
                        {
                            eyeToUse = (PassthroughCameraEye)System.Enum.Parse(typeof(PassthroughCameraEye), m_webCamTextureManager.Eye.ToString());
                        }
                        else
                        {
                            Debug.LogWarning($"[ARFlowMetaQuest] m_webCamTextureManager.Eye ('{m_webCamTextureManager.Eye}') is not a valid PassthroughCameraEye for pose. Defaulting to Left.");
                        }
                    }
                    
                    if (PassthroughCameraUtils.IsSupported)
                    {
                        cameraPose = PassthroughCameraUtils.GetCameraPoseInWorld(eyeToUse);
                    }
                    else
                    {
                        Debug.LogWarning("[ARFlowMetaQuest] Passthrough Camera API not supported, cannot get camera pose.");
                    }
                }

                float startTime = Time.realtimeSinceStartup;
                if (_cpuTex != null && _cpuTex.isReadable)
                {
                    Matrix4x4 matrix = Matrix4x4.TRS(cameraPose.position, cameraPose.rotation, Vector3.one);

                    float[] poseData = new float[12];
                    // Row 1
                    poseData[0] = matrix.m00;
                    poseData[1] = matrix.m01;
                    poseData[2] = matrix.m02;
                    poseData[3] = matrix.m03;
                    // Row 2
                    poseData[4] = matrix.m10;
                    poseData[5] = matrix.m11;
                    poseData[6] = matrix.m12;
                    poseData[7] = matrix.m13;
                    // Row 3
                    poseData[8] = matrix.m20;
                    poseData[9] = matrix.m21;
                    poseData[10] = matrix.m22;
                    poseData[11] = matrix.m23;

                    byte[] transformBytes = new byte[poseData.Length * sizeof(float)];
                    System.Buffer.BlockCopy(poseData, 0, transformBytes, 0, transformBytes.Length);

                    await _client.SendFrameAsync(new DataFrameRequest
                    {
                        Color = ByteString.CopyFrom(_cpuTex.GetRawTextureData()),
                        Transform = ByteString.CopyFrom(transformBytes)
                    });
                    _latencyMs = (Time.realtimeSinceStartup - startTime) * 1000.0f;
                    _framesSentInInterval++;
                }
                else
                {
                    Debug.LogWarning("_cpuTex is not ready for sending.");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[ARFlowMetaQuest] Error in UploadFrameAsync: {e.Message}");
            }
            finally
            {
                _isProcessingFrame = false;
            }
        }

        private async Task CopyWebCamToTextureAsync(WebCamTexture camTex)
        {
            CopyMarker.Begin();
            var rt = RenderTexture.GetTemporary(camTex.width, camTex.height, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(camTex, rt);
            await ReadRTToTextureAsync(rt);
            RenderTexture.ReleaseTemporary(rt);
            CopyMarker.End();
        }

        private async Task ReadRTToTextureAsync(RenderTexture rt)
        {
            if (rt == null || rt.width == 0 || rt.height == 0) {
                 Debug.LogError("[ARFlowMetaQuest] RenderTexture is null or invalid dimensions.");
                 return;
            }

            int scaledWidth = Mathf.Max(1, Mathf.RoundToInt(rt.width * m_renderScale));
            int scaledHeight = Mathf.Max(1, Mathf.RoundToInt(rt.height * m_renderScale));

            if (_cpuTex == null || _cpuTex.width != scaledWidth || _cpuTex.height != scaledHeight || _cpuTex.format != TextureFormat.RGB24)
            {
                if (_cpuTex != null) Destroy(_cpuTex);
                _cpuTex = new Texture2D(scaledWidth, scaledHeight, TextureFormat.RGB24, false);
            }

            RenderTexture scaledRT = RenderTexture.GetTemporary(scaledWidth, scaledHeight, 0, rt.format);
            Graphics.Blit(rt, scaledRT);

            _gpuReadbackTCS = new TaskCompletionSource<bool>();
            AsyncGPUReadback.Request(scaledRT, 0, TextureFormat.RGB24, OnCompleteReadback);
            
            RenderTexture.ReleaseTemporary(scaledRT);

            await _gpuReadbackTCS.Task;
        }

        void OnCompleteReadback(AsyncGPUReadbackRequest request)
        {
            if (request.hasError)
            {
                Debug.LogError("[ARFlowMetaQuest] GPU readback error!");
                _gpuReadbackTCS?.TrySetResult(false);
                return;
            }

            if (_cpuTex != null && _cpuTex.width == request.width && _cpuTex.height == request.height)
            {
                _cpuTex.LoadRawTextureData(request.GetData<byte>());
                _cpuTex.Apply(false);
                _gpuReadbackTCS?.TrySetResult(true);
            }
            else
            {
                Debug.LogError("[ARFlowMetaQuest] _cpuTex is null, or dimensions mismatch after GPU readback.");
                _gpuReadbackTCS?.TrySetResult(false);
            }
        }

        private void OnDestroy()
        {
#if UNITY_EDITOR
            if (_usingMainCamera && _mainCam != null) _mainCam.targetTexture = null;
#endif
            _streaming = false;
            
            if (_cpuTex != null) Destroy(_cpuTex);
            if (_mainRT != null) 
            {
                if (RenderTexture.active == _mainRT) RenderTexture.active = null;
                _mainRT.Release();
                Destroy(_mainRT);
            }
        }

        private void Log(string msg)
        {
            Debug.Log(msg);
            if (m_debugText) m_debugText.text = msg;
        }
    }
}
