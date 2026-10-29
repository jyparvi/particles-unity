using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class ParticleController : MonoBehaviour
{
    public ComputeShader computeShader;
    public Material particleMaterial;
    [Min(1)] public int particleCount;
    [Min(0.001f)] public float particleSize;
    [Min(0.0f)] public float minSpeed;
    [Min(0.0f)] public float maxSpeed;
    [Range(0.0f, 0.5f)] public float velocityStretch;
    public Vector3 d20RotationSpeed;
    [ColorUsage(true, true)] public Color nearParticleColor;
    [ColorUsage(true, true)] public Color farParticleColor;
    
    [Header("Glow")]
    public bool enableGlow;
    [ColorUsage(true, true)] public Color glowColor;
    [Range(0.0f, 10.0f)] public float glowIntensity;
    [Range(0.25f, 8.0f)] public float glowSharpness;
    
    [Header("Post Processing")]
    [Range(0.0f, 10.0f)] public float bloomAmount;
    [Range(0.0f, 1.0f)] public float filmGrainAmount;

    [SerializeField, HideInInspector] private MeshFilter d20Mesh;

    private ComputeBuffer particleBuffer;
    private ComputeBuffer d20PlaneBuffer;
    private Bounds drawBounds;
    private Matrix4x4 prevD20WorldToLocal = Matrix4x4.identity;
    private GUIStyle hudStyle;
    private VolumeProfile postProcessProfile;
    private Volume postProcessVolume;
    private Bloom bloom;
    private FilmGrain filmGrain;

    private const int ThreadGroupSize = 256;
    private const int MaxParticleCount = 1000000;

    private int kernel;
    private bool initializeParticles;
    
    // Enable the post-processing volume and store the D20 object's current transform.
    void OnEnable()
    {
        if (postProcessVolume != null)
        {
            postProcessVolume.enabled = true;
        }
 
        prevD20WorldToLocal = d20Mesh.transform.worldToLocalMatrix;
    }

    // Disable the post-processing volume.
    void OnDisable()
    {
        if (postProcessVolume != null)
        {
            postProcessVolume.enabled = false;
        }
    }

    // Initialize settings and build collision planes.
    void Start()
    {
        kernel = computeShader.FindKernel("CSMain");
        drawBounds = new Bounds(Vector3.zero, Vector3.one * 100.0f);
        prevD20WorldToLocal = d20Mesh.transform.worldToLocalMatrix;

        SetupPostProcessing();
        BuildD20Planes();
    }

    // Rotate the D20 object, update settings, and dispatch the particle simulation.
    void Update()
    {
        particleCount = Mathf.Clamp(particleCount, 1, MaxParticleCount);
        if (particleBuffer == null || particleCount != particleBuffer.count)
        {
            RebuildParticleBuffer();
        }
        UpdatePostProcessing();

        d20Mesh.transform.Rotate(d20RotationSpeed * Time.deltaTime, Space.World);

        computeShader.SetInt("initializeParticles", initializeParticles ? 1 : 0);
        computeShader.SetInt("frameIndex", Time.frameCount);
        computeShader.SetFloat("deltaTime", Time.deltaTime);
        computeShader.SetFloat("minSpeed", minSpeed);
        computeShader.SetFloat("maxSpeed", maxSpeed);

        Transform d20Transform = d20Mesh.transform;
        Matrix4x4 d20WorldToLocal = d20Transform.worldToLocalMatrix;
        particleMaterial.SetMatrix("_D20WorldToLocal", d20WorldToLocal);
        computeShader.SetMatrix("d20WorldToLocal", d20WorldToLocal);
        computeShader.SetMatrix("prevD20WorldToLocal", prevD20WorldToLocal);
        computeShader.SetMatrix("d20LocalToWorld", d20Transform.localToWorldMatrix);
        prevD20WorldToLocal = d20WorldToLocal;

        particleMaterial.SetColor("_FarParticleColor", farParticleColor);
        particleMaterial.SetColor("_NearParticleColor", nearParticleColor);
        particleMaterial.SetFloat("_ParticleSize", particleSize);
        particleMaterial.SetFloat("_VelocityStretch", velocityStretch);
        particleMaterial.SetColor("_GlowColor", glowColor);
        particleMaterial.SetFloat("_GlowIntensity", enableGlow ? glowIntensity : 0.0f);
        particleMaterial.SetFloat("_GlowSharpness", glowSharpness);

        int threadGroups = Mathf.CeilToInt((float)particleCount / ThreadGroupSize);
        computeShader.Dispatch(kernel, threadGroups, 1, 1);

        initializeParticles = false;
    }

    // Draw the particle count and current FPS.
    void OnGUI()
    {
        if (hudStyle == null)
        {
            hudStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white }
            };
        }

        float fps = 1.0f / Mathf.Max(Time.unscaledDeltaTime, 0.0001f);
        GUI.Label(
            new Rect(16.0f, 16.0f, 300.0f, 80.0f),
            $"Particles: {particleCount:N0}\nFPS: {fps:0}",
            hudStyle
        );
    }

    // Draw particle billboards using the fixed bounds.
    void LateUpdate()
    {
        Graphics.DrawProcedural(
            particleMaterial,
            drawBounds,
            MeshTopology.Triangles,
            particleCount * 6,
            1
        );
    }

    // Release GPU buffers and destroy the runtime volume and profile.
    void OnDestroy()
    {
        if (particleBuffer != null)
        {
            particleBuffer.Release();
        }
        if (d20PlaneBuffer != null)
        {
            d20PlaneBuffer.Release();
        }
        if (postProcessVolume != null)
        {
            Destroy(postProcessVolume);
        }
        if (postProcessProfile != null)
        {
            Destroy(postProcessProfile);
        }
    }

    // Recreate the particle buffer for shader initialization, then bind it to the material.
    private void RebuildParticleBuffer()
    {
        if (particleBuffer != null)
        {
            particleBuffer.Release();
        }
        
        particleBuffer = new ComputeBuffer(particleCount, sizeof(float) * 6);
        initializeParticles = true;
        particleMaterial.SetBuffer("_Particles", particleBuffer);
        computeShader.SetBuffer(kernel, "particles", particleBuffer);
        computeShader.SetInt("particleCount", particleCount);
    }

    // Build and upload normalized outward planes in the D20 object's local space.
    private void BuildD20Planes()
    {
        List<Vector4> planes = new();
        Mesh mesh = d20Mesh.sharedMesh;
        Vector3[] vertices = mesh.vertices;
        int[] triangles = mesh.triangles;

        for (int i = 0; i < triangles.Length; i += 3)
        {
            Vector3 a = vertices[triangles[i]];
            Vector3 b = vertices[triangles[i + 1]];
            Vector3 c = vertices[triangles[i + 2]];
            Vector3 normal = Vector3.Cross(b - a, c - a);

            normal.Normalize();
            if (Vector3.Dot(normal, a) < 0.0f)
            {
                normal = -normal;
            }

            planes.Add(new Vector4(normal.x, normal.y, normal.z, -Vector3.Dot(normal, a)));
        }

        d20PlaneBuffer = new ComputeBuffer(planes.Count, sizeof(float) * 4);
        d20PlaneBuffer.SetData(planes);
        computeShader.SetBuffer(kernel, "d20Planes", d20PlaneBuffer);
        computeShader.SetInt("d20PlaneCount", d20PlaneBuffer.count);
        particleMaterial.SetBuffer("_D20Planes", d20PlaneBuffer);
        particleMaterial.SetInteger("_D20PlaneCount", d20PlaneBuffer.count);
    }

    // Create a global volume and runtime profile for bloom and film grain.
    private void SetupPostProcessing()
    {
        postProcessProfile = ScriptableObject.CreateInstance<VolumeProfile>();
        bloom = postProcessProfile.Add<Bloom>();
        bloom.threshold.Override(1.0f);
        bloom.scatter.Override(0.7f);
        filmGrain = postProcessProfile.Add<FilmGrain>();
        filmGrain.type.Override(FilmGrainLookup.Thin1);
        filmGrain.response.Override(0.8f);

        postProcessVolume = gameObject.AddComponent<Volume>();
        postProcessVolume.isGlobal = true;
        postProcessVolume.priority = 100.0f;
        postProcessVolume.sharedProfile = postProcessProfile;
        UpdatePostProcessing();
    }

    // Apply bloom and film grain intensities and enable their effects when nonzero.
    private void UpdatePostProcessing()
    {
        bloomAmount = Mathf.Clamp(bloomAmount, 0.0f, 10.0f);
        bloom.active = bloomAmount > 0.0f;
        bloom.intensity.Override(bloomAmount);

        filmGrainAmount = Mathf.Clamp01(filmGrainAmount);
        filmGrain.active = filmGrainAmount > 0.0f;
        filmGrain.intensity.Override(filmGrainAmount);
    }
}
