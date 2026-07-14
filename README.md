# Gaussian Splatting for Unity 6 (URP)

![Example Scene View](image.png)

Gaussian splatting with runtime sorted rendering, standalone precomputed imports, and automatic
editor Scene view sorting.

This is a port of [MichaelMoroz/VRChatGaussianSplatting](https://github.com/MichaelMoroz/VRChatGaussianSplatting)
away from VRChat. The VRChat SDK and UdonSharp are gone; it runs on plain Unity 6 with the Universal
Render Pipeline. See [Differences from the VRChat version](#differences-from-the-vrchat-version).

## Requirements

- Unity 6 (developed against 6000.0.63f1)
- Universal Render Pipeline with **Render Graph** (compatibility mode will not work)
- A colour target **with an alpha channel** — see [Project setup](#project-setup); this is the one
  that silently ruins the picture if you get it wrong
- DX11/DX12/Vulkan for the geometry-shader path; Quest uses the no-geometry path instead

## Project setup

The splats need three things from the project itself, none of which live in this folder. Run

**`Gaussian Splatting > Setup URP Renderer`**

and it will check and fix all of them:

1. **`GaussianSplatRendererFeature` on every URP renderer.** Without it nothing draws at all — the
   splat shaders deliberately sit outside URP's own transparent pass.
2. **An alpha channel in the camera colour target.** URP's default (HDR on, HDR Precision 32-bit)
   selects `B10G11R11_UFloatPack32`, which has *no alpha*. The splats composite with
   `Blend OneMinusDstAlpha One` and stencil out fully-covered pixels by testing destination alpha, so
   with no alpha the result is wrong — and nothing warns you. The fix disables HDR, giving
   `R8G8B8A8_SRGB`, which is what the shaders were written against. (HDR at 64-bit precision also
   carries alpha, but it changes how the sRGB round trip in `ToSRGB`/`ToLinear` behaves; LDR is the
   supported path.)
3. **MSAA off, Render Graph on.** The splat pass reads the colour target mid-frame, so MSAA cannot
   be used; and the pass is a Render Graph pass, so compatibility mode never runs it.

The same checks appear as errors with a **Fix** button on the `GaussianSplatRenderer` inspector.

### First run

The sorting RenderTextures are generated per scene into `Assets/Temp/GS_<scene>/`, which is not part
of this repository — they are rebuilt on demand. Until they exist, the renderer logs

> Splat Render Order texture is not assigned. Please assign a RenderTexture.

Opening a scene with splats in it regenerates them. If the message persists, the shaders are not
compiling; see [Unity 6 porting notes](#unity-6-porting-notes).

### VR

Set up XR the usual way: **XR Plug-in Management > OpenXR**, with **Render Mode = Single Pass
Instanced**. The shaders were already written for single-pass instanced stereo, and the renderer
feature allocates its copies from the camera target descriptor and blits with
`Blitter.BlitCameraTexture`, both of which are XR-aware.

Quest standalone builds go through the existing Android pre-build pass, which swaps the
geometry-shader shaders for the no-geometry ones (Quest has no geometry shaders) and replaces the
point meshes with zero-sized quads. That path is intact but **has not been verified on a headset**.

## Workflow

1. Open `Gaussian Splatting / Import PLY Splats...`.
2. Add one or more `.ply` files and choose an output folder.
3. Configure the import options:
   - `Compute Bounding Box`
   - `sRGB Color Correction`
   - `Import Spherical Harmonics`
   - `Default SH Band`
   - `Multi-Pass Rendering`
   - `Splat Count Per Pass`
   - `Max Alpha Mask Count`
   - `Precompute Sorting`
4. Import the splats.
5. For the runtime sorted path, add the imported prefabs to the scene. The editor automatically
   creates the scene renderer and control UI when needed.
6. Use the renderer inspector to collect splats, choose single or combined rendering, resize sorting
   textures, and tune material/render settings.

### Import Option Notes

- `sRGB Color Correction` adds two extra full-screen colour copies. It fixes transparency and
  compositing behaviour, but it is heavier. Without it, the renderer falls back to back-to-front
  blending, which also means multi-pass rendering will not work correctly.
- `Multi-Pass Rendering` splits a splat into sequential chunks. This can improve VR rendering
  performance for large splats.
- `Max Alpha Mask Count` inserts optional alpha-mask passes between multi-pass chunks to occlude
  later chunks behind opaque geometry. Each mask costs one more colour copy, so it is a tradeoff.
- `Precompute Sorting` bakes direction-based order into the imported data so the splat can render
  standalone, outside the `GaussianSplatRenderer` path, but it uses much more texture memory and can
  introduce artifacts.
- `.ply` files larger than 2 GB are not supported. Large imports are limited by available RAM.

## Differences from the VRChat version

Removed, because they have no meaning outside VRChat:

- **Networked/synced controls.** Udon's `[UdonSynced]`, `RequestSerialization`, `OnDeserialization`
  and the master-only gate have no standalone equivalent, so every control is now local.
- **VRC Light Volumes.** The shader sampled `_UdonLightVolume*` globals that only a VRChat world
  populates, so the keyword could only ever be off.
- **The photo camera and mirror paths.** `_VRChatCameraMode` and `_VRChatMirrorMode` are always 0
  outside VRChat, so these branches were dead — but they still cost a second render-order texture, a
  second combined colour texture, and a full second combine pass.

Changed:

- **Interaction.** `QualityToggle` and `TurnOnToggle` used VRChat's gaze-press `Interact()`. They now
  implement `IPointerClickHandler`, i.e. an ordinary screen click, which needs a `Collider` on the
  object and a `PhysicsRaycaster` on the camera (the UI builder adds both). They also expose a
  parameterless method, so a uGUI Button or an XR ray interactor can drive them.
- **Sorting is driven from `RenderPipelineManager.beginCameraRendering`,** not `Update`. The sorted
  render order is global material state, so it has to be rebuilt for whichever camera is about to be
  drawn. As a side effect Scene view sorting now works — under an SRP, `Camera.onPreCull` never
  fires, so it had been dead.
- **`GrabPass` is gone.** See below.

Kept: `QualityToggle` and `TurnOnToggle` (as click targets), the Android/Quest no-geometry build
pass, and the combined rendering mode.

Not provided: the LOD path. `GaussianSplatLODObject` is a stub in the upstream repository and its
shaders (`LODChunkSelect`, `LODCombineData`) are not part of it.

## Unity 6 porting notes

Two things broke on the way from Unity 2022.3 (which is what VRChat uses) to Unity 6, in ways that
did not look like what they were.

### `#pragma` in include files

Unity ignores Unity-specific `#pragma` directives in files pulled in with a plain `#include`;
`#include_with_pragmas` exists to opt into them. **Unity 2022.3 honoured them from a plain `#include`
anyway. Unity 6 does not**, and drops the snippet:

> Both vertex and fragment programs must be present in a shader snippet. Excluding it from
> compilation.

`GS.cginc` and `FullscreenCommon.cginc` hold the entry-point pragmas for seven shaders, so all seven
were dead on Unity 6 — and the symptom surfaced a long way away: the shaders reported
`isSupported = false`, so their materials reported `HasProperty("_GS_Positions") == false`, so the
renderer found zero splats, so it never created the sorting RenderTextures, so you got *"Splat Render
Order texture is not assigned."* Both files are now included with `#include_with_pragmas`.

### Magenta materials

`Shader.isSupported` does **not** tell you whether URP can draw a shader. The Standard shader
compiles and reports `isSupported = true`; it simply has no pass URP knows (`ForwardBase` /
`ForwardAdd`), so URP substitutes the error material. The check that matters is whether any pass
carries a `LightMode` tag URP recognises — or no `LightMode` tag at all, which counts as
`SRPDefaultUnlit`.

Materials in this package are all URP-drawable, and `VerifyMaterials` (below) keeps them that way.

## Verification

Three batch-mode entry points, useful in CI and for checking a change did not quietly break
something:

```bash
Unity.exe -batchmode -quit -projectPath . -executeMethod \
  GaussianSplatting.Editor.GaussianSplatCiChecks.CheckShaders
Unity.exe -batchmode -quit -projectPath . -executeMethod \
  GaussianSplatting.Editor.GaussianSplatCiChecks.VerifyMaterials
Unity.exe -batchmode -quit -projectPath . -executeMethod \
  GaussianSplatting.Editor.GaussianSplatCiChecks.VerifyScenes
```

- **`CheckShaders`** — fails on shader errors, *and* on the "must be present in a shader snippet"
  warning, which is only a warning but means the shader renders nothing at all.
- **`VerifyMaterials`** — fails on any material URP cannot draw, judged by pass `LightMode` tags
  rather than `Shader.isSupported` (see [Magenta materials](#magenta-materials)).
- **`VerifyScenes`** — opens each example scene and checks it is actually playable: no missing
  scripts, a camera, an `EventSystem` with `InputSystemUIInputModule`, a `PhysicsRaycaster`, and UI
  buttons whose `onClick` points at something. Grepping the YAML cannot see any of this — components
  are stored by script GUID, not by name.

None of these need a graphics device. Note that `Shader.isSupported` *does*, which is why it is not
used: under `-batchmode` it reports false for every shader.

## Rendering pipeline

- Runtime rendering is sorted-only, front-to-back, with sorted render-order textures.
- SH selection is controlled numerically through `_SHBand`, clamped by the textures the imported
  material actually has.
- Splats should not rely on MSAA.

### Replacing GrabPass under URP

URP has no `GrabPass`, and it draws the entire transparent queue in a single `DrawObjectsPass`, so
nothing can be injected between two render queues. `_CameraOpaqueTexture` is no help either: URP
copies it once, *before* transparents, and the splat chain needs the colour target mid-sequence.

The way out is that URP's transparent pass only picks up three `LightMode` tags. Giving the splat
shaders their own tags takes them out of URP's pass entirely, and `GaussianSplatRendererFeature` then
owns the whole sequence at `AfterRenderingTransparents`, reproducing the GrabPass chain one-for-one:

```
copy colour -> _GS_LinearBackground        (was GrabPass "_LinearBackground")
ToSRGB                                     rewrites the target in gamma space, alpha 0
splat chunk
  copy colour -> _GS_GrabTexture           (was GrabPass {})
  AlphaDepthMask                           stencils out fully covered pixels
splat chunk ...
copy colour -> _GS_SRGBBackground
ToLinear                                   subtracts the background back out, returns to linear
```

`_GS_LinearBackground` stays bound all the way to `ToLinear`, which reads it a second time — that is
what lets it subtract the background's contribution out of the front-to-back accumulation, and it is
why the copy is not folded into `ToSRGB`.

The render queues and the material array are untouched, so the importer, the combiner and every
imported `.mat` keep working exactly as before. `GaussianSplatRuntimeRegistry` carries the one thing
the feature cannot work out for itself: which render queues hold an `AlphaDepthMask`, since the
importer derives them from each splat's material array.

The pass is an *unsafe* Render Graph pass, because a raster pass cannot read and write the same
texture — which would force a pass split per copy.

### Cursed radix sort

The runtime sorter is a radix sort built on mipmap-based prefix sums, sorting 4 bits at a time over
16-value digits. It was written this way because VRChat offers no compute shaders, buffers or
atomics; it is kept because it works and needs no compute support. `Sorting Steps` trades ordering
accuracy against cost.

### Ellipsoid screen projection

Splats are rendered as projected billboards, but the ellipse is fitted by sampling the projected
tangent outline of the ellipsoid rather than using the center-Jacobian affine approximation that
standard 3DGS uses. That approximation shows up as blur, shape drift and scene inconsistency,
especially in VR where the camera can be very close to the splats with a very large field of view.
Numerically this recovers the same projected ellipse as exact ellipsoid-projection approaches such as
"Projecting Gaussian Ellipsoids While Avoiding Affine Projection Approximation" (arXiv:2411.07579v2),
via a float-only outline-sampling fit. It also extends naturally to distorted camera models.

## Editor Scene view sorting

- Automatic for `GaussianSplatObject`, editor-only, with its own transient sorting resources.
- Skips standalone precomputed-sorting materials.

## Credits

- `.PLY` importer adapted from [aras-p's UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting)
- Ported from [MichaelMoroz's VRChatGaussianSplatting](https://github.com/MichaelMoroz/VRChatGaussianSplatting),
  itself a heavily modified version of [lambdalemon's gaussian splats](https://github.com/lambdalemon/vrcsplat)
- The radix sort uses [d4rkpl4y3r's mipmap prefix sum trick](https://github.com/d4rkc0d3r/CompactSparseTextureDemo)

## License

MIT, Copyright (c) 2025 Mykhailo Moroz. See `LICENSE`.
