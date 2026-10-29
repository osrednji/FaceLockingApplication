using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenCvSharp;

namespace FaceLockApp.Services;

public class FaceSample
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public DateTime CapturedAt { get; set; } = DateTime.Now;
    public byte[] DisplayImagePng { get; set; } = Array.Empty<byte>();
    public byte[] NormalizedImagePng { get; set; } = Array.Empty<byte>();
    public float[] SpatialHistogram { get; set; } = Array.Empty<float>();
}

public class OwnerGallery
{
    public List<FaceSample> Samples { get; set; } = new();
}

public class FaceEngine : IDisposable
{
    private readonly CascadeClassifier? _frontalAlt2Cascade;
    private readonly CascadeClassifier? _frontalDefaultCascade;
    private readonly CascadeClassifier? _profileCascade;

    private const int StandardSize = 160;
    private const int GridRows = 8;
    private const int GridCols = 8;
    private const int HistBins = 16;

    public FaceEngine()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;

        string alt2Path = Path.Combine(baseDir, "haarcascade_frontalface_alt2.xml");
        string defaultPath = Path.Combine(baseDir, "haarcascade_frontalface_default.xml");
        string profilePath = Path.Combine(baseDir, "haarcascade_profileface.xml");

        if (File.Exists(alt2Path))
            _frontalAlt2Cascade = new CascadeClassifier(alt2Path);

        if (File.Exists(defaultPath))
            _frontalDefaultCascade = new CascadeClassifier(defaultPath);

        if (File.Exists(profilePath))
            _profileCascade = new CascadeClassifier(profilePath);

        if (_frontalAlt2Cascade == null && _frontalDefaultCascade == null)
        {
            throw new FileNotFoundException("No frontal face cascade classifiers found.");
        }
    }

    /// <summary>
    /// Multi-angle face detection supporting frontal, semi-turned, and side profile faces (looking at other monitors).
    /// </summary>
    public Rect[] DetectFaces(Mat bgrFrame)
    {
        using var gray = new Mat();
        Cv2.CvtColor(bgrFrame, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.EqualizeHist(gray, gray);

        var detectedList = new List<Rect>();

        // 1. Frontal Alt2 (handles tilt and slight/moderate angles much better than default)
        if (_frontalAlt2Cascade != null)
        {
            var alt2Faces = _frontalAlt2Cascade.DetectMultiScale(
                gray, scaleFactor: 1.1, minNeighbors: 4, minSize: new OpenCvSharp.Size(60, 60));
            detectedList.AddRange(alt2Faces);
        }

        // 2. Default frontal fallback
        if (detectedList.Count == 0 && _frontalDefaultCascade != null)
        {
            var defaultFaces = _frontalDefaultCascade.DetectMultiScale(
                gray, scaleFactor: 1.15, minNeighbors: 4, minSize: new OpenCvSharp.Size(60, 60));
            detectedList.AddRange(defaultFaces);
        }

        // 3. Profile faces (head turned towards left monitor)
        if (_profileCascade != null)
        {
            var leftProfiles = _profileCascade.DetectMultiScale(
                gray, scaleFactor: 1.15, minNeighbors: 4, minSize: new OpenCvSharp.Size(60, 60));
            detectedList.AddRange(leftProfiles);

            // 4. Profile faces (head turned towards right monitor - detected via horizontal flip)
            using var flipped = new Mat();
            Cv2.Flip(gray, flipped, FlipMode.Y);
            var rightProfiles = _profileCascade.DetectMultiScale(
                flipped, scaleFactor: 1.15, minNeighbors: 4, minSize: new OpenCvSharp.Size(60, 60));

            int frameWidth = gray.Width;
            foreach (var r in rightProfiles)
            {
                // Unflip horizontal coordinate
                int origX = frameWidth - r.X - r.Width;
                detectedList.Add(new Rect(origX, r.Y, r.Width, r.Height));
            }
        }

        return NonMaxSuppression(detectedList, iouThreshold: 0.35);
    }

    public FaceSample CreateSample(Mat bgrFrame, Rect faceRect)
    {
        // 1. High-resolution color thumbnail for UI gallery
        using var faceColorCrop = new Mat(bgrFrame, faceRect);
        using var thumbMat = new Mat();
        Cv2.Resize(faceColorCrop, thumbMat, new OpenCvSharp.Size(120, 120));
        byte[] displayBytes = thumbMat.ToBytes(".png");

        // 2. Normalized grayscale face for recognition
        using var gray = new Mat();
        Cv2.CvtColor(bgrFrame, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.EqualizeHist(gray, gray);

        using var faceCrop = new Mat(gray, faceRect);
        using var resized = new Mat();
        Cv2.Resize(faceCrop, resized, new OpenCvSharp.Size(StandardSize, StandardSize));

        float[] hist = ComputeSpatialHistogram(resized);
        byte[] normalizedBytes = resized.ToBytes(".png");

        return new FaceSample
        {
            Id = Guid.NewGuid().ToString(),
            CapturedAt = DateTime.Now,
            DisplayImagePng = displayBytes,
            NormalizedImagePng = normalizedBytes,
            SpatialHistogram = hist
        };
    }

    public (bool isMatch, double score, string? matchedSampleId) VerifyAgainstGallery(
        Mat bgrFrame, 
        Rect faceRect, 
        OwnerGallery gallery, 
        double threshold = 0.52)
    {
        if (gallery == null || gallery.Samples.Count == 0)
            return (false, 0, null);

        using var gray = new Mat();
        Cv2.CvtColor(bgrFrame, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.EqualizeHist(gray, gray);

        using var faceCrop = new Mat(gray, faceRect);
        using var resized = new Mat();
        Cv2.Resize(faceCrop, resized, new OpenCvSharp.Size(StandardSize, StandardSize));

        float[] currentHist = ComputeSpatialHistogram(resized);

        double maxScore = 0.0;
        string? bestSampleId = null;

        foreach (var sample in gallery.Samples)
        {
            if (sample.NormalizedImagePng == null || sample.NormalizedImagePng.Length == 0)
                continue;

            // 1. Template Normalized Cross-Correlation
            using var sampleMat = Mat.FromImageData(sample.NormalizedImagePng, ImreadModes.Grayscale);
            using var result = new Mat();
            Cv2.MatchTemplate(resized, sampleMat, result, TemplateMatchModes.CCoeffNormed);
            result.MinMaxLoc(out _, out double maxVal);

            double templateScore = Math.Clamp(maxVal, 0.0, 1.0);

            // 2. Spatial Grid Histogram Correlation
            double histScore = ComputeHistogramCorrelation(sample.SpatialHistogram, currentHist);

            // Blended match score
            double sampleScore = (0.50 * templateScore) + (0.50 * histScore);

            if (sampleScore > maxScore)
            {
                maxScore = sampleScore;
                bestSampleId = sample.Id;
            }
        }

        bool match = maxScore >= threshold;
        return (match, maxScore, bestSampleId);
    }

    private float[] ComputeSpatialHistogram(Mat normalizedGray)
    {
        int cellW = StandardSize / GridCols;
        int cellH = StandardSize / GridRows;
        float[] fullHist = new float[GridRows * GridCols * HistBins];

        int[] channels = { 0 };
        int[] histSize = { HistBins };
        Rangef[] ranges = { new Rangef(0, 256) };

        int offset = 0;
        for (int r = 0; r < GridRows; r++)
        {
            for (int c = 0; c < GridCols; c++)
            {
                var cellRect = new Rect(c * cellW, r * cellH, cellW, cellH);
                using var cell = new Mat(normalizedGray, cellRect);
                using var hist = new Mat();
                Cv2.CalcHist(new[] { cell }, channels, null, hist, 1, histSize, ranges);
                Cv2.Normalize(hist, hist, 1, 0, NormTypes.L1);

                for (int b = 0; b < HistBins; b++)
                {
                    fullHist[offset++] = hist.At<float>(b);
                }
            }
        }

        return fullHist;
    }

    private double ComputeHistogramCorrelation(float[] h1, float[] h2)
    {
        if (h1 == null || h2 == null || h1.Length != h2.Length || h1.Length == 0)
            return 0;

        double sum1 = 0, sum2 = 0;
        int n = h1.Length;
        for (int i = 0; i < n; i++)
        {
            sum1 += h1[i];
            sum2 += h2[i];
        }
        double mean1 = sum1 / n;
        double mean2 = sum2 / n;

        double num = 0, den1 = 0, den2 = 0;
        for (int i = 0; i < n; i++)
        {
            double diff1 = h1[i] - mean1;
            double diff2 = h2[i] - mean2;
            num += diff1 * diff2;
            den1 += diff1 * diff1;
            den2 += diff2 * diff2;
        }

        double denom = Math.Sqrt(den1 * den2);
        if (denom < 1e-7) return 0;

        double corr = num / denom;
        return Math.Clamp((corr + 1.0) / 2.0, 0.0, 1.0); // Map [-1, 1] to [0, 1]
    }

    private static Rect[] NonMaxSuppression(List<Rect> boxes, double iouThreshold)
    {
        if (boxes.Count <= 1) return boxes.ToArray();

        // Sort descending by area
        var sorted = boxes.OrderByDescending(b => b.Width * b.Height).ToList();
        var selected = new List<Rect>();

        while (sorted.Count > 0)
        {
            var current = sorted[0];
            selected.Add(current);
            sorted.RemoveAt(0);

            sorted.RemoveAll(candidate =>
            {
                int x1 = Math.Max(current.X, candidate.X);
                int y1 = Math.Max(current.Y, candidate.Y);
                int x2 = Math.Min(current.X + current.Width, candidate.X + candidate.Width);
                int y2 = Math.Min(current.Y + current.Height, candidate.Y + candidate.Height);

                int interWidth = Math.Max(0, x2 - x1);
                int interHeight = Math.Max(0, y2 - y1);
                int interArea = interWidth * interHeight;

                int areaA = current.Width * current.Height;
                int areaB = candidate.Width * candidate.Height;
                int unionArea = areaA + areaB - interArea;

                if (unionArea <= 0) return false;
                double iou = (double)interArea / unionArea;
                return iou > iouThreshold;
            });
        }

        return selected.ToArray();
    }

    public void Dispose()
    {
        _frontalAlt2Cascade?.Dispose();
        _frontalDefaultCascade?.Dispose();
        _profileCascade?.Dispose();
    }
}
