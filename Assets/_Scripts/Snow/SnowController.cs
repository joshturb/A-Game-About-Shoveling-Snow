using UnityEngine;

[System.Serializable]
public struct SnowSettings
{
    public int snowDetail;
    public int smoothingIterations;

    public FastNoiseLite.NoiseType noiseType;
    public FastNoiseLite.FractalType fractalType;

    public int fractalOctaves;
    public float fractalLacunarity;
    public float fractalGain;

    public float scale;
    public float minHeight;
    public float maxHeight;

    public Material snowMaterial;

    public static SnowSettings Default => new()
    {
        snowDetail = 1,
        noiseType = FastNoiseLite.NoiseType.OpenSimplex2,
        fractalType = FastNoiseLite.FractalType.FBm,
        fractalOctaves = 5,
        fractalLacunarity = 2f,
        fractalGain = 0.5f,
        scale = 30f,
        minHeight = 0f,
        maxHeight = 2f,
        snowMaterial = null
    };
}

public class SnowController : MonoBehaviour
{
    public static SnowController Instance;

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
    private FastNoiseLite noise;
    private int totalVerts;
    private int clearedVerts;

    void Awake()
    {
        if (Instance != null && Instance != this) 
        { 
            Destroy(gameObject); 
            return; 
        }
        Instance = this;
        
        int seed = Random.Range(int.MinValue, int.MaxValue);
        noise = new(seed);

        noise.SetNoiseType(snowSettings.noiseType);
        noise.SetFractalType(snowSettings.fractalType);
        noise.SetFractalOctaves(snowSettings.fractalOctaves);
        noise.SetFractalGain(snowSettings.fractalGain);
        noise.SetFractalLacunarity(snowSettings.fractalLacunarity);
        noise.SetFrequency(1f / Mathf.Max(0.0001f, snowSettings.scale));
    }

    public float SampleNoise(Vector3 worldPos)
    {
        float n = noise.GetNoise(worldPos.x, worldPos.z);     // sample directly in world units
        float t = Mathf.Clamp01(n * 0.5f + 0.5f);            // map [-1..1] -> [0..1] (safe even if overshoot)
        return Mathf.Lerp(snowSettings.minHeight, snowSettings.maxHeight, t);
    }

    public void RegisterField(int vertexCount, int initialClearedCount)
    {
        totalVerts += vertexCount;
        clearedVerts += initialClearedCount;
    }

    public void ApplyClearedDelta(int deltaCleared)
    {
        clearedVerts = Mathf.Clamp(clearedVerts + deltaCleared, 0, totalVerts);
    }

    public float GetClearedPercent()
        => totalVerts == 0 ? 0f : clearedVerts / (float)totalVerts * 100f;

    [ContextMenu("cleared%")]
    public void test()
    {
        print(GetClearedPercent());
    }
}
