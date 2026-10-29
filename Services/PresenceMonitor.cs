using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using OpenCvSharp;

namespace FaceLockApp.Services;

public enum MonitorState
{
    Stopped,
    Arming,
    ActiveMonitoring,
    Triggered
}

public class FrameEventArgs : EventArgs
{
    public Mat Frame { get; }
    public Rect[] DetectedFaces { get; }
    public Rect? OwnerFace { get; }
    public double MatchScore { get; }

    public FrameEventArgs(Mat frame, Rect[] detectedFaces, Rect? ownerFace, double matchScore)
    {
        Frame = frame;
        DetectedFaces = detectedFaces;
        OwnerFace = ownerFace;
        MatchScore = matchScore;
    }
}

public class PresenceMonitor : IDisposable
{
    private readonly FaceEngine _faceEngine;
    private readonly SecurityService _securityService;
    private VideoCapture? _capture;
    private CancellationTokenSource? _cts;
    private Task? _monitorTask;

    private OwnerGallery _gallery = new();
    private int _consecutiveMisses = 0;

    public bool IsAuthenticated { get; private set; } = false;
    public MonitorState State { get; private set; } = MonitorState.Stopped;
    public int ArmingCountdownSeconds { get; private set; } = 3;
    public bool LogoutInsteadOfLock { get; set; } = false;
    public double MatchThreshold { get; set; } = 0.52;
    public int LockDelaySeconds { get; set; } = 4; // Missed consecutive checks before locking

    public event EventHandler<FrameEventArgs>? FrameArrived;
    public event EventHandler<MonitorState>? StateChanged;
    public event EventHandler<string>? StatusMessageChanged;
    public event EventHandler<int>? ArmingCountdownTick;

    public PresenceMonitor(FaceEngine faceEngine, SecurityService securityService)
    {
        _faceEngine = faceEngine;
        _securityService = securityService;
        ReloadGallery();

        // Listen for Windows session unlock (e.g. user returns to workstation)
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    public void ReloadGallery()
    {
        _gallery = _securityService.LoadGallery();
    }

    public OwnerGallery Gallery => _gallery;
    public bool HasRegisteredOwner => _gallery.Samples.Count > 0;

    public void SetAuthenticated(bool authenticated)
    {
        IsAuthenticated = authenticated;
        if (!authenticated)
        {
            // When not authenticated, recognition and locking MUST NOT work
            // and 3-second timer MUST NOT be enabled
            SetState(MonitorState.Stopped);
            _consecutiveMisses = 0;
            StatusMessageChanged?.Invoke(this, "Locked: Enter master password to activate.");
        }
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionUnlock)
        {
            // Only re-arm if the application was authenticated!
            if (IsAuthenticated && HasRegisteredOwner && State != MonitorState.Stopped)
            {
                StartArmingCountdown();
            }
        }
        else if (e.Reason == SessionSwitchReason.SessionLock)
        {
            _consecutiveMisses = 0;
        }
    }

    public void StartCamera()
    {
        if (_cts != null && !_cts.IsCancellationRequested)
            return;

        _cts = new CancellationTokenSource();
        _consecutiveMisses = 0;

        _capture = new VideoCapture(0, VideoCaptureAPIs.DSHOW);
        if (!_capture.IsOpened())
        {
            _capture.Dispose();
            _capture = new VideoCapture(0);
        }

        if (!_capture.IsOpened())
        {
            StatusMessageChanged?.Invoke(this, "Error: Could not open webcam.");
            return;
        }

        _capture.FrameWidth = 640;
        _capture.FrameHeight = 480;

        _monitorTask = Task.Run(() => ProcessingLoop(_cts.Token));
    }

    public void StopCamera()
    {
        _cts?.Cancel();
        _monitorTask?.Wait(1000);
        _capture?.Release();
        _capture?.Dispose();
        _capture = null;
        _cts?.Dispose();
        _cts = null;

        SetState(MonitorState.Stopped);
        StatusMessageChanged?.Invoke(this, "Camera Stopped");
    }

    public void StartArmingCountdown()
    {
        // Guard rule: if master password is NOT entered, 3s timer is NOT enabled!
        if (!IsAuthenticated)
        {
            SetState(MonitorState.Stopped);
            StatusMessageChanged?.Invoke(this, "Access Denied: Master password required to arm.");
            return;
        }

        if (!HasRegisteredOwner)
        {
            SetState(MonitorState.Stopped);
            StatusMessageChanged?.Invoke(this, "No owner photos enrolled. Please take a picture first.");
            return;
        }

        SetState(MonitorState.Arming);
        _consecutiveMisses = 0;

        Task.Run(async () =>
        {
            for (int i = 3; i > 0; i--)
            {
                if (!IsAuthenticated || State != MonitorState.Arming) return;

                ArmingCountdownSeconds = i;
                ArmingCountdownTick?.Invoke(this, i);
                StatusMessageChanged?.Invoke(this, $"System arming in {i} seconds...");
                await Task.Delay(1000);
            }

            if (!IsAuthenticated || State != MonitorState.Arming) return;

            ArmingCountdownSeconds = 0;
            ArmingCountdownTick?.Invoke(this, 0);
            SetState(MonitorState.ActiveMonitoring);
            StatusMessageChanged?.Invoke(this, "Protection Armed & Active");
        });
    }

    private void SetState(MonitorState newState)
    {
        State = newState;
        StateChanged?.Invoke(this, newState);
    }

    public (bool success, string message, FaceSample? sample) CaptureAndAddSample(Mat currentFrame)
    {
        var faces = _faceEngine.DetectFaces(currentFrame);
        if (faces.Length == 0)
        {
            return (false, "No face detected in the frame. Please look towards the camera.", null);
        }
        if (faces.Length > 1)
        {
            return (false, "Multiple faces detected. Please make sure only you are in frame.", null);
        }

        var sample = _faceEngine.CreateSample(currentFrame, faces[0]);
        _securityService.AddSample(sample);
        ReloadGallery();

        return (true, "Picture added to owner profile!", sample);
    }

    public bool DeleteSample(string sampleId)
    {
        bool deleted = _securityService.DeleteSample(sampleId);
        if (deleted)
        {
            ReloadGallery();
            if (!HasRegisteredOwner && State == MonitorState.ActiveMonitoring)
            {
                SetState(MonitorState.Stopped);
                StatusMessageChanged?.Invoke(this, "All photos deleted. Monitoring stopped.");
            }
        }
        return deleted;
    }

    private void ProcessingLoop(CancellationToken token)
    {
        using var frame = new Mat();
        DateTime lastDecisionTime = DateTime.MinValue;

        while (!token.IsCancellationRequested)
        {
            if (_capture == null || !_capture.Read(frame) || frame.Empty())
            {
                Thread.Sleep(50);
                continue;
            }

            var detectedFaces = _faceEngine.DetectFaces(frame);
            Rect? ownerFace = null;
            double highestScore = 0;

            // Only recognize faces if authenticated and we have registered owner photos
            if (IsAuthenticated && _gallery.Samples.Count > 0)
            {
                foreach (var face in detectedFaces)
                {
                    var (isMatch, score, _) = _faceEngine.VerifyAgainstGallery(frame, face, _gallery, MatchThreshold);
                    if (score > highestScore)
                    {
                        highestScore = score;
                    }

                    if (isMatch)
                    {
                        ownerFace = face;
                        break;
                    }
                }
            }

            // Emit frame for UI presentation
            FrameArrived?.Invoke(this, new FrameEventArgs(frame.Clone(), detectedFaces, ownerFace, highestScore));

            // CRITICAL REQUIREMENT:
            // "when master password is not entered, 3 seconds timer should not be enabled, and recognition and locking should not work"
            if (!IsAuthenticated)
            {
                Thread.Sleep(40);
                continue;
            }

            // Active Presence surveillance logic
            if (State == MonitorState.ActiveMonitoring)
            {
                if ((DateTime.Now - lastDecisionTime).TotalMilliseconds >= 900)
                {
                    lastDecisionTime = DateTime.Now;

                    if (ownerFace != null)
                    {
                        _consecutiveMisses = 0;
                        StatusMessageChanged?.Invoke(this, $"Owner Recognized (Confidence: {(int)(highestScore * 100)}%)");
                    }
                    else
                    {
                        _consecutiveMisses++;
                        int remaining = LockDelaySeconds - _consecutiveMisses;

                        if (remaining > 0)
                        {
                            StatusMessageChanged?.Invoke(this, $"Warning: Owner not recognized! Locking in {remaining}s...");
                        }
                        else
                        {
                            // Trigger Lock or Logout
                            StatusMessageChanged?.Invoke(this, "Locking workstation...");
                            SetState(MonitorState.Triggered);

                            Win32Api.TriggerAction(LogoutInsteadOfLock);

                            _consecutiveMisses = 0;
                        }
                    }
                }
            }

            Thread.Sleep(40);
        }
    }

    public void Dispose()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        StopCamera();
    }
}
