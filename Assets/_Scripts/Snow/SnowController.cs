using System;
using UnityEngine;

[System.Serializable]
public struct SnowSettings
{
    public float minHeight;
    public float maxHeight;
    [Header("Layer 1 (Drifts)")]
    public FastNoiseLite.NoiseType noiseType1;
    public FastNoiseLite.FractalType fractalType1;
    public int octaves1;
    public float lacunarity1;
    public float gain1;
    public float scale1;
    public float weight1;
    public float power1;

    [Header("Layer 2 (Ripples)")]
    public FastNoiseLite.NoiseType noiseType2;
    public FastNoiseLite.FractalType fractalType2;
    public int octaves2;
    public float lacunarity2;
    public float gain2;
    public float scale2;
    public float weight2;
    public float power2;

    [Header("Layer 3 (Ridges/Peaks)")]
    public FastNoiseLite.NoiseType noiseType3;
    public FastNoiseLite.FractalType fractalType3;
    public int octaves3;
    public float lacunarity3;
    public float gain3;
    public float scale3;
    public float weight3;
    public float power3;

    [Header("Final Height")]
    public float minSnowHeight;
    public float maxSnowHeight;

    public static SnowSettings Default => new()
    {
        minHeight = 0,
        maxHeight = 2,

        noiseType1 = FastNoiseLite.NoiseType.OpenSimplex2,
        fractalType1 = FastNoiseLite.FractalType.FBm,
        octaves1 = 4,
        lacunarity1 = 2.0f,
        gain1 = 0.5f,
        scale1 = 120f,
        weight1 = 0.70f,
        power1 = 2.0f,

        noiseType2 = FastNoiseLite.NoiseType.OpenSimplex2,
        fractalType2 = FastNoiseLite.FractalType.FBm,
        octaves2 = 3,
        lacunarity2 = 2.3f,
        gain2 = 0.45f,
        scale2 = 28f,
        weight2 = 0.25f,
        power2 = 1.0f,

        noiseType3 = FastNoiseLite.NoiseType.OpenSimplex2,
        fractalType3 = FastNoiseLite.FractalType.Ridged,
        octaves3 = 2,
        lacunarity3 = 2.1f,
        gain3 = 0.65f,
        scale3 = 60f,
        weight3 = 0.12f,
        power3 = 1.0f,

        minSnowHeight = 0.1f,
        maxSnowHeight = 3f,
    };
}

public class SnowController : MonoBehaviour
{
    public static SnowController Instance;
    public event Action<float> OnGlobalProgressUpdated;

    [Header("Snow Settings")]
    [SerializeField] private SnowSettings snowSettings = SnowSettings.Default;

    [Header("Snow Stats")]
    [SerializeField] private float snowCleared;
    [SerializeField] private float snowRemaining;

    // Getters
    public SnowSettings GetSnowSettings() => snowSettings;
    public float GetSnowCleared() => snowCleared;
    public float GetSnowRemaining() => snowRemaining;
 
    // Private Variables
    private FastNoiseLite n1;
    private FastNoiseLite n2;
    private FastNoiseLite n3;
    private int totalVerts;
    private int clearedVerts;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;

        int seed = UnityEngine.Random.Range(int.MinValue, int.MaxValue);

        n1 = new FastNoiseLite(seed);
        n2 = new FastNoiseLite(unchecked(seed ^ (int)0x9E3779B9));
        n3 = new FastNoiseLite(unchecked(seed ^ (int)0xBB67AE85));

        ConfigureNoise(n1, snowSettings.noiseType1, snowSettings.fractalType1, snowSettings.octaves1, snowSettings.lacunarity1, snowSettings.gain1, snowSettings.scale1);
        ConfigureNoise(n2, snowSettings.noiseType2, snowSettings.fractalType2, snowSettings.octaves2, snowSettings.lacunarity2, snowSettings.gain2, snowSettings.scale2);
        ConfigureNoise(n3, snowSettings.noiseType3, snowSettings.fractalType3, snowSettings.octaves3, snowSettings.lacunarity3, snowSettings.gain3, snowSettings.scale3);
    }

    private static void ConfigureNoise(FastNoiseLite n, FastNoiseLite.NoiseType type, FastNoiseLite.FractalType fractal, int oct, float lac, float gain, float scale)
    {
        n.SetNoiseType(type);
        n.SetFractalType(fractal);
        n.SetFractalOctaves(Mathf.Max(1, oct));
        n.SetFractalLacunarity(lac);
        n.SetFractalGain(gain);
        n.SetFrequency(1f / Mathf.Max(0.0001f, scale));
    }

    public float SampleNoise(Vector3 worldPos)
    {
        float t1 = Mathf.Clamp01(n1.GetNoise(worldPos.x, worldPos.z) * 0.5f + 0.5f);
        float t2 = Mathf.Clamp01(n2.GetNoise(worldPos.x, worldPos.z) * 0.5f + 0.5f);
        float t3 = Mathf.Clamp01(n3.GetNoise(worldPos.x, worldPos.z) * 0.5f + 0.5f);

        if (snowSettings.power1 != 1f) t1 = Mathf.Pow(t1, Mathf.Max(0.0001f, snowSettings.power1));
        if (snowSettings.power2 != 1f) t2 = Mathf.Pow(t2, Mathf.Max(0.0001f, snowSettings.power2));
        if (snowSettings.power3 != 1f) t3 = Mathf.Pow(t3, Mathf.Max(0.0001f, snowSettings.power3));

        float w1 = Mathf.Max(0f, snowSettings.weight1);
        float w2 = Mathf.Max(0f, snowSettings.weight2);
        float w3 = Mathf.Max(0f, snowSettings.weight3);
        float wSum = Mathf.Max(1e-6f, w1 + w2 + w3);

        float t = (t1 * w1 + t2 * w2 + t3 * w3) / wSum;
        t = Mathf.Clamp01(t);

        return Mathf.Lerp(snowSettings.minSnowHeight, snowSettings.maxSnowHeight, t);
    }

    public void RegisterField(int vertexCount, int initialClearedCount)
    {
        totalVerts += vertexCount;
        clearedVerts += initialClearedCount;
    }

    public void ApplyClearedDelta(int deltaCleared)
    {
        clearedVerts = Mathf.Clamp(clearedVerts + deltaCleared, 0, totalVerts);
        OnGlobalProgressUpdated?.Invoke(totalVerts == 0 ? 0f : clearedVerts / (float)totalVerts * 100f);
    }

    public float GetClearedPercent()
        => totalVerts == 0 ? 0f : clearedVerts / (float)totalVerts * 100f;

    [ContextMenu("cleared%")]
    public void test()
    {
        print(GetClearedPercent());
    }
}
