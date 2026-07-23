using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
using UnityEngine.Rendering;
#endif

public class RadixSort : MonoBehaviour
{
    [SerializeField] public Material computeKeyValues;
    [SerializeField] public Material radixSort;
    [SerializeField] public Material copySortedOrder;
    [Tooltip("Compute-shader sort used at runtime when supported; the blit materials above stay as the fallback. Auto-assigned in the editor.")]
    [SerializeField] public ComputeShader radixSortCompute;
    [Tooltip("Shader that copies the compute-sorted buffer into the render-order texture. Auto-assigned in the editor.")]
    [SerializeField] public Shader copyOrderFromBufferShader;

    // Per-sort scratch is rebound from the serialized bucket arrays; never serialize these holders.
    [System.NonSerialized] public RenderTexture keyValues0;
    [System.NonSerialized] public RenderTexture keyValues1;
    [System.NonSerialized] public RenderTexture histograms;
    [System.NonSerialized] public RenderTexture prefixSums;
    [HideInInspector] [SerializeField] public RenderTexture[] keyValues0ByBucket;
    [HideInInspector] [SerializeField] public RenderTexture[] keyValues1ByBucket;
    [HideInInspector] [SerializeField] public RenderTexture[] histogramsByBucket;
    [HideInInspector] [SerializeField] public RenderTexture[] prefixSumsByBucket;

    [System.NonSerialized] public int elementCount = 1024 * 1024;

    public const int BitsPerPass = 4;
    public const int SortStartBit = 7;
    public const int MaxKeyBits = 31;
    public const int TotalSortPasses = 6;
    private const int groupSizeLog2 = 4;

    public bool UseBucketResources(int tier)
    {
        if (!TryGetTierTexture(keyValues0ByBucket, tier, out RenderTexture kv0)
            || !TryGetTierTexture(keyValues1ByBucket, tier, out RenderTexture kv1)
            || !TryGetTierTexture(histogramsByBucket, tier, out RenderTexture hist)
            || !TryGetTierTexture(prefixSumsByBucket, tier, out RenderTexture prefix))
        {
            return false;
        }

        keyValues0 = kv0;
        keyValues1 = kv1;
        histograms = hist;
        prefixSums = prefix;
        return true;
    }

    public bool BindDefaultBucketResources()
    {
        if (keyValues0 != null && keyValues1 != null && histograms != null && prefixSums != null)
        {
            return true;
        }
        int maxTier = keyValues0ByBucket != null ? keyValues0ByBucket.Length - 1 : -1;
        for (int tier = maxTier; tier >= 0; tier--)
        {
            if (UseBucketResources(tier))
            {
                return true;
            }
        }
        return false;
    }

    // Pure check (no mutation) used by the renderer to verify a tier is fully baked before committing a swap.
    public bool HasBucketResources(int tier)
    {
        return TryGetTierTexture(keyValues0ByBucket, tier, out RenderTexture kv0)
            && TryGetTierTexture(keyValues1ByBucket, tier, out RenderTexture kv1)
            && TryGetTierTexture(histogramsByBucket, tier, out RenderTexture hist)
            && TryGetTierTexture(prefixSumsByBucket, tier, out RenderTexture prefix);
    }

    static bool TryGetTierTexture(RenderTexture[] textures, int tier, out RenderTexture texture)
    {
        texture = textures != null && tier >= 0 && tier < textures.Length ? textures[tier] : null;
        return texture != null;
    }

    // Compute path: 256 threads x 4 elements per group, 8-bit digits over the same bits 7..30 the
    // blit path sorts (bit 31 is the sign bit of the positive-float key, always zero).
    const int ComputeThreads = 256;
    const int ComputeGroupElements = 1024;
    static readonly int[] ComputeShiftBits = { 7, 15, 23 };

    GraphicsBuffer _keysA;
    GraphicsBuffer _keysB;
    GraphicsBuffer _groupHistograms;
    Material _copyOrderFromBufferMat;
    int _kernelInitKeys = -1;
    int _kernelBuildHistogram = -1;
    int _kernelScanHistograms = -1;
    int _kernelScatter = -1;

#if UNITY_EDITOR
    static Material _editorCopySortedOrderMaterial;
#endif

    // Game: run a complete sort immediately and copy the order. The compute path does the same
    // work as the blit path (same key bits, same tie order, same output layout) in three linear
    // passes instead of a per-element mip-pyramid gather per round; the blit path remains as the
    // fallback for platforms without compute support or unassigned assets.
    public void RunFullSort(RenderTexture renderOrder, int slice)
    {
        if (TryRunFullSortCompute(renderOrder))
        {
            return;
        }
        if (!BeginSortInternal(false))
        {
            return;
        }
        RunSortPassesInternal(false);
        CopySortedOrderInternal(renderOrder, slice, false);
    }

    // True when the runtime sort will take the compute path. The blit scratch RTs are then never
    // bound as blit targets, so the renderer skips pre-creating them on the GPU; the editor
    // preview and the blit fallback auto-create them on their first Blit instead.
    public bool ComputeSortAvailable()
    {
        return radixSortCompute != null && copyOrderFromBufferShader != null && computeKeyValues != null
            && SystemInfo.supportsComputeShaders;
    }

    void OnDisable()
    {
        ReleaseComputeSortResources();
    }

    void ReleaseComputeSortResources()
    {
        if (_keysA != null) { _keysA.Release(); _keysA = null; }
        if (_keysB != null) { _keysB.Release(); _keysB = null; }
        if (_groupHistograms != null) { _groupHistograms.Release(); _groupHistograms = null; }
        if (_copyOrderFromBufferMat != null)
        {
            // The compute path also runs for game-view repaints in edit mode, where Destroy is illegal.
            if (Application.isPlaying) Destroy(_copyOrderFromBufferMat);
            else DestroyImmediate(_copyOrderFromBufferMat);
            _copyOrderFromBufferMat = null;
        }
    }

    bool EnsureComputeSortResources(int count, int groupCount)
    {
        if (_copyOrderFromBufferMat == null)
        {
            _copyOrderFromBufferMat = new Material(copyOrderFromBufferShader);
            _copyOrderFromBufferMat.name = "GaussianSplatCopyOrderFromBuffer";
            _copyOrderFromBufferMat.hideFlags = HideFlags.HideAndDontSave;
        }
        if (_kernelInitKeys < 0)
        {
            _kernelInitKeys = radixSortCompute.FindKernel("InitKeys");
            _kernelBuildHistogram = radixSortCompute.FindKernel("BuildHistogram");
            _kernelScanHistograms = radixSortCompute.FindKernel("ScanHistograms");
            _kernelScatter = radixSortCompute.FindKernel("Scatter");
        }
        if (_keysA == null || _keysA.count < count)
        {
            if (_keysA != null) _keysA.Release();
            if (_keysB != null) _keysB.Release();
            _keysA = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 8);
            _keysB = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 8);
        }
        int histogramCount = groupCount * 256;
        if (_groupHistograms == null || _groupHistograms.count < histogramCount)
        {
            if (_groupHistograms != null) _groupHistograms.Release();
            _groupHistograms = new GraphicsBuffer(GraphicsBuffer.Target.Structured, histogramCount, 4);
        }
        return _keysA != null && _keysB != null && _groupHistograms != null;
    }

    bool TryRunFullSortCompute(RenderTexture renderOrder)
    {
        if (!ComputeSortAvailable() || renderOrder == null || elementCount <= 0)
        {
            return false;
        }
        Texture positions = computeKeyValues.GetTexture("_GS_Positions");
        if (positions == null)
        {
            return false;
        }
        int count = elementCount;
        int groupCount = (count + ComputeGroupElements - 1) / ComputeGroupElements;
        if (!EnsureComputeSortResources(count, groupCount))
        {
            return false;
        }

        ComputeShader sort = radixSortCompute;
        sort.SetInt("_ElementCount", count);
        sort.SetInt("_GroupCount", groupCount);
        // The key bindings mirror what GaussianSplatRenderer already set on the key-value material.
        sort.SetVector("_CameraPos", computeKeyValues.GetVector("_CameraPos"));
        sort.SetInt("_GS_Positions_CoordMask", computeKeyValues.GetInt("_GS_Positions_CoordMask"));
        sort.SetInt("_GS_Positions_CoordShift", computeKeyValues.GetInt("_GS_Positions_CoordShift"));

        sort.SetTexture(_kernelInitKeys, "_GS_Positions", positions);
        sort.SetBuffer(_kernelInitKeys, "_KeysDst", _keysA);
        sort.Dispatch(_kernelInitKeys, (count + ComputeThreads - 1) / ComputeThreads, 1, 1);

        GraphicsBuffer source = _keysA;
        GraphicsBuffer destination = _keysB;
        for (int pass = 0; pass < ComputeShiftBits.Length; pass++)
        {
            sort.SetInt("_ShiftBits", ComputeShiftBits[pass]);

            sort.SetBuffer(_kernelBuildHistogram, "_KeysSrc", source);
            sort.SetBuffer(_kernelBuildHistogram, "_GroupHistograms", _groupHistograms);
            sort.Dispatch(_kernelBuildHistogram, groupCount, 1, 1);

            sort.SetBuffer(_kernelScanHistograms, "_GroupHistograms", _groupHistograms);
            sort.Dispatch(_kernelScanHistograms, 1, 1, 1);

            sort.SetBuffer(_kernelScatter, "_KeysSrc", source);
            sort.SetBuffer(_kernelScatter, "_KeysDst", destination);
            sort.SetBuffer(_kernelScatter, "_GroupHistograms", _groupHistograms);
            sort.Dispatch(_kernelScatter, groupCount, 1, 1);

            GraphicsBuffer swap = source;
            source = destination;
            destination = swap;
        }

        _copyOrderFromBufferMat.SetBuffer("_SortedKeys", source);
        _copyOrderFromBufferMat.SetInt("_ElementCount", count);
        Graphics.Blit(null, renderOrder, _copyOrderFromBufferMat, 0);
        return true;
    }

#if UNITY_EDITOR
    // Play-mode sanity check: runs the compute sort with the current bindings, reads the result
    // back, and verifies the sorted key bits are non-decreasing and the ids are a permutation.
    [ContextMenu("Validate Compute Sort")]
    void ValidateComputeSort()
    {
        if (!Application.isPlaying)
        {
            Debug.LogError("RadixSort: validation needs play mode with a bound splat (the key-value material must have positions and a camera position).");
            return;
        }
        // The compute path never binds the blit scratch RTs, so rebind them here just for the
        // scratch dimensions (they are NonSerialized holders rebound from the bucket arrays).
        if (keyValues0 == null && !BindDefaultBucketResources())
        {
            Debug.LogError("RadixSort: generated sort resources are missing, cannot size the validation scratch.");
            return;
        }
        RenderTexture scratch = RenderTexture.GetTemporary(keyValues0.width, keyValues0.height, 0, RenderTextureFormat.RFloat);
        bool ran = TryRunFullSortCompute(scratch);
        RenderTexture.ReleaseTemporary(scratch);
        if (!ran)
        {
            Debug.LogError("RadixSort: compute sort did not run (missing assets, no compute support, or nothing bound).");
            return;
        }
        int count = elementCount;
        // After an odd number of passes the final data sits in _keysB.
        GraphicsBuffer final = ComputeShiftBits.Length % 2 == 1 ? _keysB : _keysA;
        uint[] data = new uint[count * 2];
        final.GetData(data, 0, 0, count * 2);
        const uint sortedBitsMask = 0x7FFFFF80u; // bits 7..30, the bits the sort compares
        bool[] seen = new bool[count];
        int order_errors = 0, permutation_errors = 0;
        uint previous = 0;
        for (int i = 0; i < count; i++)
        {
            uint key = data[i * 2] & sortedBitsMask;
            uint id = data[i * 2 + 1];
            if (key < previous) order_errors++;
            previous = key;
            if (id >= (uint)count || seen[id]) permutation_errors++;
            else seen[id] = true;
        }
        if (order_errors == 0 && permutation_errors == 0)
        {
            Debug.Log("RadixSort: compute sort PASSED validation (" + count + " elements, keys non-decreasing, ids form a permutation).");
        }
        else
        {
            Debug.LogError("RadixSort: compute sort FAILED validation: " + order_errors + " order errors, " + permutation_errors + " permutation errors out of " + count + " elements.");
        }
    }
#endif

#if UNITY_EDITOR
    // Editor previews: full sort + copy every frame for the given camera slice.
    public void RunFullSortForEditor(RenderTexture renderOrder, int slice)
    {
        if (!BeginSortInternal(true))
        {
            return;
        }
        RunSortPassesInternal(true);
        CopySortedOrderInternal(renderOrder, slice, true);
    }
#endif

    bool BeginSortInternal(bool useEditorOps)
    {
        if (!BindDefaultBucketResources())
        {
            Debug.LogError("RadixSort: generated sort resources are missing. Refresh the GaussianSplatRenderer in the editor.");
            return false;
        }
        // Runtime uniforms that vary each frame
        setStaticUniforms();

        // 1. Evaluate key values
#if UNITY_EDITOR
        if (useEditorOps)
        {
            Graphics.Blit(null, keyValues0, computeKeyValues);
        }
        else
#endif
        {
            Graphics.Blit(null, keyValues0, computeKeyValues);
        }

        radixSort.SetTexture("_PrefixSums", prefixSums);
        radixSort.SetTexture("_Histograms", histograms);
        return true;
    }

    void RunSortPassesInternal(bool useEditorOps)
    {
        int currentBit = SortStartBit;
        for (int i = 0; i < TotalSortPasses && currentBit < MaxKeyBits; i++)
        {
            radixSort.SetTexture("_KeyValues", keyValues0);
            radixSort.SetInt("_CurrentBit", currentBit);

#if UNITY_EDITOR
            if (useEditorOps)
            {
                Graphics.Blit(null, histograms, radixSort, 0);
                radixSort.SetTexture("_Histograms", histograms);
                Graphics.Blit(null, prefixSums, radixSort, 1);
            }
            else
#endif
            {
                Graphics.Blit(null, histograms, radixSort, 0);
                radixSort.SetTexture("_Histograms", histograms);
                Graphics.Blit(null, prefixSums, radixSort, 1);
            }

            prefixSums.GenerateMips();

#if UNITY_EDITOR
            if (useEditorOps)
            {
                Graphics.Blit(null, keyValues1, radixSort, 2);
            }
            else
#endif
            {
                Graphics.Blit(null, keyValues1, radixSort, 2);
            }

            // Ping-pong the buffers
            RenderTexture temp = keyValues0;
            keyValues0 = keyValues1;
            keyValues1 = temp;

            currentBit += BitsPerPass;
        }
    }

#if UNITY_EDITOR
    static Material GetEditorCopySortedOrderMaterial()
    {
        if (_editorCopySortedOrderMaterial != null)
        {
            return _editorCopySortedOrderMaterial;
        }

        Shader shader = Shader.Find("Hidden/GaussianSplatting/CopyRenderOrder");
        if (shader == null)
        {
            return null;
        }

        _editorCopySortedOrderMaterial = new Material(shader);
        _editorCopySortedOrderMaterial.name = "GaussianSplatRadixSortCopyRenderOrder";
        _editorCopySortedOrderMaterial.hideFlags = HideFlags.HideAndDontSave;
        return _editorCopySortedOrderMaterial;
    }

    static void DrawFullscreenQuad(Material material)
    {
        if (material == null || !material.SetPass(0))
        {
            return;
        }

        GL.PushMatrix();
        GL.LoadOrtho();
        GL.Begin(GL.QUADS);
        GL.TexCoord2(0.0f, 0.0f);
        GL.Vertex3(0.0f, 0.0f, 0.0f);
        GL.TexCoord2(1.0f, 0.0f);
        GL.Vertex3(1.0f, 0.0f, 0.0f);
        GL.TexCoord2(1.0f, 1.0f);
        GL.Vertex3(1.0f, 1.0f, 0.0f);
        GL.TexCoord2(0.0f, 1.0f);
        GL.Vertex3(0.0f, 1.0f, 0.0f);
        GL.End();
        GL.PopMatrix();
    }
#endif

    void CopySortedOrderInternal(RenderTexture target, int slice, bool useEditorOps)
    {
        if (target == null)
        {
            return;
        }

#if UNITY_EDITOR
        if (useEditorOps)
        {
            Material copyMaterial = GetEditorCopySortedOrderMaterial();
            if (copyMaterial == null)
            {
                Debug.LogError("RadixSort: Missing Hidden/GaussianSplatting/CopyRenderOrder shader for editor sorting.");
                return;
            }

            copyMaterial.SetTexture("_KeyValues", keyValues0);

            Graphics.Blit(null, target, copyMaterial, 0);
            return;
        }
#endif

        copySortedOrder.SetTexture("_KeyValues", keyValues0);
        Graphics.Blit(null, target, copySortedOrder, 0);
    }

    private void setStaticUniforms()
    {
        int _OptimalPOT = Mathf.NextPowerOfTwo(Mathf.CeilToInt(elementCount));
        int _OptimalPOTLog2 = Mathf.CeilToInt(Mathf.Log(_OptimalPOT, 2));
        int _OptimalImageSizeLog2Y = _OptimalPOTLog2 / 2;
        int _OptimalImageSizeLog2X = _OptimalImageSizeLog2Y + _OptimalPOTLog2 % 2;
        int _OptimalImageSizeX = 1 << _OptimalImageSizeLog2X;
        int _OptimalImageSizeY = 1 << _OptimalImageSizeLog2Y;

        if(keyValues0 == null || keyValues0.width < _OptimalImageSizeX || keyValues0.height < _OptimalImageSizeY) {
            int currentWidth = keyValues0 != null ? keyValues0.width : 0;
            int currentHeight = keyValues0 != null ? keyValues0.height : 0;
            Debug.LogError($"RadixSort: Texture size ({currentWidth}x{currentHeight}) is smaller than required ({_OptimalImageSizeX}x{_OptimalImageSizeY}). Please resize the textures.");
            return;
        }
        int _HistogramPOTLog2 = Mathf.Max(0, _OptimalPOTLog2 - groupSizeLog2);
        int _HistogramImageSizeLog2Y = _HistogramPOTLog2 / 2;
        int _HistogramImageSizeLog2X = _HistogramImageSizeLog2Y + _HistogramPOTLog2 % 2;
        int _HistogramImageSizeX = 1 << _HistogramImageSizeLog2X;
        int _HistogramImageSizeY = 1 << _HistogramImageSizeLog2Y;
        if(histograms == null || histograms.width < _HistogramImageSizeX || histograms.height < _HistogramImageSizeY) {
            int currentWidth = histograms != null ? histograms.width : 0;
            int currentHeight = histograms != null ? histograms.height : 0;
            Debug.LogError($"RadixSort: Histogram texture size ({currentWidth}x{currentHeight}) is smaller than required ({_HistogramImageSizeX}x{_HistogramImageSizeY}). Please resize the textures.");
            return;
        }

        Vector2 scale = new Vector2((float)_OptimalImageSizeX / keyValues0.width, (float)_OptimalImageSizeY / keyValues0.height);
        Vector2 histogramScale = new Vector2((float)_HistogramImageSizeX / histograms.width, (float)_HistogramImageSizeY / histograms.height);

        computeKeyValues.SetInt("_BitsPerStep", BitsPerPass);
        computeKeyValues.SetInt("_GroupSize", groupSizeLog2);
        computeKeyValues.SetInt("_ElementCount", elementCount);
        computeKeyValues.SetInt("_ImageSizeLog2X", _OptimalImageSizeLog2X);
        computeKeyValues.SetInt("_ImageSizeLog2Y", _OptimalImageSizeLog2Y);
        computeKeyValues.SetInt("_ImageElementsLog2", _OptimalPOTLog2);
        computeKeyValues.SetVector("_Scale", scale);

        radixSort.SetInt("_BitsPerStep", BitsPerPass);
        radixSort.SetInt("_GroupSize", groupSizeLog2);
        radixSort.SetInt("_ElementCount", elementCount);
        radixSort.SetInt("_ImageSizeLog2X", _OptimalImageSizeLog2X);
        radixSort.SetInt("_ImageSizeLog2Y", _OptimalImageSizeLog2Y);
        radixSort.SetInt("_ImageElementsLog2", _OptimalPOTLog2);
        radixSort.SetVector("_Scale", scale);
        radixSort.SetVector("_HistogramScale", histogramScale);
    }
}
