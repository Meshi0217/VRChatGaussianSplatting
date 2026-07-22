# Gaussian Splatting for Unity 6 (URP)

<img src="VRCGS_Logo.png" alt="Logo" width="320">

![Example Scene View](image.png)

Gaussian splatting for plain Unity 6 with the Universal Render Pipeline: runtime camera-sorted
rendering, a `.ply` / `.spz` importer with an LOD pyramid, a gallery and control UI, automatic editor
Scene-view sorting, and terrain-collider / re-import tools.

This is a port of [MichaelMoroz/VRChatGaussianSplatting](https://github.com/MichaelMoroz/VRChatGaussianSplatting) (v4)
away from VRChat. The VRChat SDK and UdonSharp are gone; everything runs as ordinary MonoBehaviours
under URP. See [Differences from the VRChat version](#differences-from-the-vrchat-version).

**[日本語はこちら](#gaussian-splatting-for-unity-6-urp日本語)**

## Features

- Sorted-only runtime rendering through `GaussianSplatRenderer` — all active splats are combined
  into world space and sorted together as one renderer
- Importer for `.ply` and `.spz` splats: `Gaussian Splatting > Import Splats...`
- Import modes: **LOD** (combined, with a level-of-detail hierarchy) and **Standalone**
  (self-rendering, precomputed sorting)
- **LOD hierarchy** — an import-time LOD pyramid; the runtime selects per-chunk detail against a
  scene-wide, per-platform splat budget, with quality tiers and an LOD Splat Cap slider
- **Gallery** UI: list splats and show one at a time
- Automatic scene renderer + world-space control UI when splats are present
- Automatic, editor-only Scene-view sorting for every `GaussianSplatObject`
- Editor tools: **terrain collider** generation, and **re-import** (exact / edit-settings) from a
  splat's stored import metadata
- Android/Quest build conversion to the no-geometry splat shaders
- **Light Volumes** — REDSIM's [VRCLightVolumes](https://github.com/REDSIM/VRCLightVolumes) (v2.1.3, MIT)
  vendored under `LightVolumes/` and running as plain MonoBehaviours; splats can be tinted by the
  baked ambient volume at each splat position
- Bilingual UI (English / 日本語)

## Requirements

- Unity 6 (developed against 6000.0.63f1)
- Universal Render Pipeline with **Render Graph** (compatibility mode will not work)
- The **Editor Coroutines** package (`com.unity.editorcoroutines`) — required by the Light Volumes
  baking tools
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
   `R8G8B8A8_SRGB`, which is what the shaders were written against.
3. **MSAA off, Render Graph on.** The splat pass reads the colour target mid-frame, so MSAA cannot
   be used; and the pass is a Render Graph pass, so compatibility mode never runs it.

The same checks appear as errors with a **Fix** button on the `GaussianSplatRenderer` inspector.

### Generated resources

The shared sorting / combine RenderTextures and draw-pass meshes are pregenerated assets committed
under `RTPool/` — the editor bake assigns them, and nothing is created at runtime. Per-scene combined
resources are generated into `Assets/Temp/GS_<scene>/` on demand. If the renderer logs *"Splat Render
Order texture is not assigned"*, the shaders are usually not compiling; see
[Unity 6 porting notes](#unity-6-porting-notes).

### VR

Set up XR the usual way: **XR Plug-in Management > OpenXR**, with **Render Mode = Single Pass
Instanced**. The shaders were already written for single-pass instanced stereo, and the renderer
feature allocates its copies from the camera target descriptor and blits with
`Blitter.BlitCameraTexture`, both of which are XR-aware.

Quest standalone builds go through the Android pre-build pass, which swaps the geometry-shader
shaders for the no-geometry ones (Quest has no geometry shaders) and replaces the point meshes with
zero-sized quads. `Gaussian Splatting > Convert Open Scene To Android No-Geometry (preview)` applies
the same conversion in the editor for testing. That path is intact but **has not been verified on a
headset**.

## Workflow

1. Open `Gaussian Splatting > Import Splats...`.
2. Add one or more `.ply` / `.spz` files, choose an output folder, and pick an **Import Mode**.
3. Configure the import options and import.
4. Drag the imported prefabs into the scene. The editor automatically creates the scene
   `GaussianSplatRenderer` and the control UI when needed.
5. Tune material/render settings from the renderer inspector or the in-scene UI.

### Import modes

- **LOD** (default) — a combined `GaussianSplatObject` with a downsampled LOD pyramid. The combiner
  selects per-chunk detail by camera distance and a scene-wide splat budget; full-detail LOD0 is
  preserved up close.
- **Standalone** — a self-rendering mesh + material with **precomputed direction-based sorting**
  baked in. Renders without `GaussianSplatRenderer`, but uses more texture memory and can introduce
  artifacts.

### Import option notes

- `Import Spherical Harmonics` + `Max SH Band` — memory scales with the chosen band; disabled
  forces SH0. `SH Compression` picks `None` (RGB565), `BC1` (4 bpp) or `BC7` (8 bpp).
- **Transform / cleanup**: `Crop To Bounds` (preview box handle), `Horizon Alignment` /
  `Wall Alignment` (pick points in the preview), `Normalize Size`.
- **LOD mode**: `Chunk Size`, `LOD Resampling Rate` / `LOD Reused Splats` tune the pyramid.
- **Standalone mode only**: `sRGB Color Correction` is the exact colour/compositing path (two extra
  full-screen colour copies). Without it the splat falls back to back-to-front blending and
  multi-pass rendering will not work. `Multi-Pass Rendering` + `Max Alpha Mask Count` split the
  splat into sequential chunks with optional occlusion masks — each mask costs one more colour copy.
- `.ply` files larger than 2 GB are not supported. Large imports are limited by available RAM.

## In-scene UI

The editor creates a world-space control canvas automatically when splats are present and the scene
has none. Since the port is single-user, **every control is local** — nothing is networked.

- **Gallery**: add splat objects to the UI's gallery list; when the list has entries, only the
  selected splat renders. Objects not in the list are never touched. (The master-lock toggle remains
  as a UI switch, but there is no instance master to lock against.)
- Quality presets (Very Low / Low / Medium / High), SH Band, Light Volumes toggle + intensity,
  Gaussian Scale, Alpha Cutoff / Cull, Antialiasing, Camera Quantization, LOD Splat Cap (when LOD
  splats are present), and an **Advanced Settings** toggle.
- Language: English / 日本語.
- The 3D click toggles (`QualityToggle`, `TurnOnToggle`) need a `Collider` on the object and a
  `PhysicsRaycaster` on the camera; the UI builder wires both up.

## Terrain collider

`Gaussian Splatting > Generate Terrain Collider...` (or the `GaussianSplatObject` context menu)
rasterizes a splat into a heightmap on the GPU and builds a Unity `TerrainData` + `TerrainCollider`.
It is a 2.5D (top-down) collider — good for ground/terrain, not overhangs or interiors.

## Differences from the VRChat version

Removed, because they have no meaning outside VRChat:

- **Networked/synced controls.** The upstream synced the gallery selection and master lock across
  the instance; here every control is local and the local user always counts as the master.
- **The photo camera and mirror paths.** `_VRChatCameraMode` and `_VRChatMirrorMode` are always 0
  outside VRChat, so these branches were dead — but they still cost a second render-order texture
  per pool bucket, a second combined colour pass, and per-frame photo-camera bookkeeping.

Changed:

- **Interaction.** `QualityToggle` and `TurnOnToggle` used VRChat's gaze-press `Interact()`. They now
  implement `IPointerClickHandler`, i.e. an ordinary screen click. UI buttons are wired to the
  MonoBehaviour methods directly with persistent listeners instead of Udon's
  `SendCustomEvent(string)` indirection.
- **Sorting is driven from `RenderPipelineManager.beginCameraRendering`,** not `Update`. The sorted
  render order is global material state, so it has to be rebuilt for whichever camera is about to be
  drawn. As a side effect Scene view sorting works — under an SRP, `Camera.onPreCull` never fires.
- **`GrabPass` is gone.** See [Replacing GrabPass under URP](#replacing-grabpass-under-urp).
- **Light Volumes run without Udon.** REDSIM's package guards every VRChat dependency behind
  `#if UDONSHARP`, so the vendored copy under `LightVolumes/` compiles as plain MonoBehaviours
  as-is — the `LightVolumeManager` binds the same `_UdonLightVolume*` shader globals that the
  splat shaders sample. Baking works with the Progressive Lightmapper (or Bakery if installed).
  The VRChat-only extras (AudioLink/TVGI integrations, ASE shaders, attribution prefabs) are not
  vendored.

Kept: the importer, the LOD system, the gallery UI, combined rendering, the terrain-collider and
re-import tools, and the Android/Quest no-geometry build pass.

## Unity 6 porting notes

Two things broke on the way from Unity 2022.3 (which is what VRChat uses) to Unity 6, in ways that
did not look like what they were.

### `#pragma` in include files

Unity ignores Unity-specific `#pragma` directives in files pulled in with a plain `#include`;
`#include_with_pragmas` exists to opt into them. **Unity 2022.3 honoured them from a plain `#include`
anyway. Unity 6 does not**, and drops the snippet:

> Both vertex and fragment programs must be present in a shader snippet. Excluding it from
> compilation.

`GS.cginc` and `FullscreenCommon.cginc` hold the entry-point pragmas for several shaders, so all of
them were dead on Unity 6 — and the symptom surfaced a long way away: the shaders reported
`isSupported = false`, so their materials reported `HasProperty("_GS_Positions") == false`, so the
renderer found zero splats, so it never bound the sorting RenderTextures, so you got *"Splat Render
Order texture is not assigned."* Both files are included with `#include_with_pragmas`.

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

The example scenes and prefabs were cleaned of leftover VRChat components with the tools under
`Gaussian Splatting > Cleanup`; they are also what to reach for after pulling upstream changes.

## Rendering pipeline

- Runtime rendering is sorted-only, front-to-back, with sorted render-order textures.
- SH selection is controlled numerically through `_SHBand`, clamped by the textures the imported
  material actually has.
- Splats should not rely on MSAA; the renderer disables it on the camera it sorts for.

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

- Ported from [MichaelMoroz's VRChatGaussianSplatting](https://github.com/MichaelMoroz/VRChatGaussianSplatting) (v4),
  itself a heavily modified version of [lambdalemon's gaussian splats](https://github.com/lambdalemon/vrcsplat)
- `.PLY` importer originally adapted from [aras-p's UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting)
- The radix sort uses [d4rkpl4y3r's mipmap prefix sum trick](https://github.com/d4rkc0d3r/CompactSparseTextureDemo)
- Light Volumes are [REDSIM's VRCLightVolumes](https://github.com/REDSIM/VRCLightVolumes) (MIT),
  vendored under `LightVolumes/`

## License

MIT, Copyright (c) 2025 Mykhailo Moroz. See `LICENSE`.

---

# Gaussian Splatting for Unity 6 (URP)（日本語）

素の Unity 6 + Universal Render Pipeline で動くガウシアンスプラッティングです。ランタイムのカメラ
ソートレンダリング、LOD ピラミッド付きの `.ply` / `.spz` インポーター、ギャラリー・コントロール UI、
エディタ Scene ビューの自動ソート、地形コライダー / 再インポートツールを備えています。

[MichaelMoroz/VRChatGaussianSplatting](https://github.com/MichaelMoroz/VRChatGaussianSplatting)（v4）を
VRChat から切り離した移植版です。VRChat SDK と UdonSharp は含まれず、すべて通常の MonoBehaviour と
して URP 上で動作します。

## 必要環境

- Unity 6（6000.0.63f1 で開発）
- Universal Render Pipeline + **Render Graph**（互換モードでは動きません）
- **アルファチャンネル付き**のカラーターゲット — 下の「プロジェクト設定」参照。ここを間違えると
  警告なしに絵が壊れます
- ジオメトリシェーダー経路は DX11/DX12/Vulkan。Quest は No-Geometry 経路を使います

## プロジェクト設定

**`Gaussian Splatting > Setup URP Renderer`** を実行すると、以下の 3 点を検査して修正します。

1. **すべての URP レンダラーに `GaussianSplatRendererFeature` を追加。** これがないと何も描画され
   ません（スプラットシェーダーは意図的に URP の透明パスの外にあります）。
2. **カメラのカラーターゲットにアルファチャンネルを確保。** URP のデフォルト（HDR オン・32bit）は
   アルファなしの `B10G11R11_UFloatPack32` になります。スプラットは `Blend OneMinusDstAlpha One`
   で合成し、デスティネーションアルファで被覆済みピクセルをステンシル除外するため、アルファなしでは
   結果が壊れます。修正では HDR を無効化して `R8G8B8A8_SRGB` にします。
3. **MSAA オフ・Render Graph オン。**

同じ検査は `GaussianSplatRenderer` のインスペクタにも **Fix** ボタン付きで表示されます。

### VR

通常どおり **XR Plug-in Management > OpenXR**、**Render Mode = Single Pass Instanced** で設定して
ください。シェーダーはシングルパスインスタンス立体視前提で書かれており、RendererFeature も XR 対応
の API でコピー/ブリットを行います。Quest ビルドは Android プリビルドパスで No-Geometry シェーダー
に変換されます（`Gaussian Splatting > Convert Open Scene To Android No-Geometry (preview)` でエディタ
内でも試せます）。**実機での検証はまだ行っていません。**

## 使い方

1. `Gaussian Splatting > Import Splats...` を開く。
2. `.ply` / `.spz` ファイルを追加し、出力フォルダと**インポートモード**を選ぶ。
3. オプションを設定してインポート。
4. 生成されたプレハブをシーンに配置。シーンの `GaussianSplatRenderer` とコントロール UI は必要に
   応じて自動生成されます。
5. レンダラーのインスペクタまたはシーン内 UI で設定を調整。

### インポートモード

- **LOD**（デフォルト）— ダウンサンプルした LOD ピラミッド付きの結合 `GaussianSplatObject`。
  カメラ距離とシーン全体のスプラット予算でチャンクごとの詳細度を選択し、近距離ではフル詳細の
  LOD0 が維持されます。
- **Standalone** — 方向ベースの事前計算ソートを焼き込んだ自己描画メッシュ + マテリアル。
  `GaussianSplatRenderer` なしで描画できますが、テクスチャメモリを多く使い、アーティファクトが
  出ることがあります。

## シーン内 UI

スプラットがあるシーンには世界空間のコントロールキャンバスが自動生成されます。この移植版は
シングルユーザーなので、**すべてのコントロールはローカル**です（ネットワーク同期はありません）。

- **ギャラリー**: UI のギャラリーリストに登録したスプラットのうち、選択中の 1 つだけを表示。
  リスト外のオブジェクトには一切触れません。
- 品質プリセット / SH バンド / ガウススケール / アルファカットオフ・カリング / アンチエイリアス /
  LOD スプラット上限 / 詳細設定トグル / 言語（英語・日本語）
- 3D クリックトグル（`QualityToggle` / `TurnOnToggle`）はオブジェクトの `Collider` とカメラの
  `PhysicsRaycaster` が必要です（UI ビルダーが自動配線します）。

## VRChat 版との違い

- **ネットワーク同期を削除** — ギャラリー選択・マスターロックの同期は廃止し、全操作ローカル。
- **Light Volumes は Udon なしで動作** — REDSIM のパッケージ（v2.1.3、MIT）を `LightVolumes/` に
  同梱。VRChat 依存はすべて `#if UDONSHARP` ガード内のため素の MonoBehaviour としてそのまま
  コンパイルされ、`LightVolumeManager` がスプラットシェーダーの参照するグローバル値を設定します。
  ベイクは Progressive Lightmapper（Bakery 導入済みなら Bakery も）で可能。要
  `com.unity.editorcoroutines` パッケージ。
- **フォトカメラ / ミラー経路を削除** — VRChat 外では常に無効なのに、プールバケットごとの追加
  レンダーオーダーテクスチャや追加カラーパスを消費していました。
- **操作系を変更** — `Interact()`（注視プレス）は `IPointerClickHandler`（通常クリック）に。UI
  ボタンは Udon の `SendCustomEvent` 間接呼び出しではなく MonoBehaviour メソッドへの永続リスナー。
- **ソートを `beginCameraRendering` 駆動に変更** — 描画直前のカメラに合わせてソートを再構築。
  副作用として Scene ビューのソートも機能します。
- **`GrabPass` を全廃** — URP には GrabPass がないため、`GaussianSplatRendererFeature` が
  `AfterRenderingTransparents` で GrabPass 連鎖を一対一で再現します（詳細は英語版
  「Replacing GrabPass under URP」参照）。

維持: インポーター、LOD システム、ギャラリー UI、結合レンダリング、地形コライダー / 再インポート
ツール、Android/Quest No-Geometry ビルドパス。

## 検証

CI 向けのバッチモードエントリポイントが 3 つあります（`CheckShaders` / `VerifyMaterials` /
`VerifyScenes`、コマンドは英語版参照）。シェーダーのコンパイル、URP で描画不能な（マゼンタになる）
マテリアル、サンプルシーンの再生可能性（欠落スクリプト・EventSystem・PhysicsRaycaster・ボタン配線）
をそれぞれ検査します。いずれもグラフィックスデバイス不要です。

## ライセンス

MIT, Copyright (c) 2025 Mykhailo Moroz. `LICENSE` を参照してください。
