# Vendored: MinecraftSkinRender

This directory is a **vendored copy** of [Coloryr/MinecraftSkinRender](https://github.com/Coloryr/MinecraftSkinRender)
(the core library plus its OpenGL backend, which shipped as the `MinecraftSkinRender` and
`MinecraftSkinRender.OpenGL` NuGet packages) so we can pose individual body parts — something
the packaged renderer cannot express, see below.

- **Upstream:** https://github.com/Coloryr/MinecraftSkinRender
- **Commit:** `8da2c913da91b84c6ae7d3f5b817bab6bcbe9505` (the tree NuGet `1.2.0` was built from)
- **License:** MIT. `LICENSE` is retained unmodified, as MIT requires.
- **Scope:** only the two projects we use. `MinecraftSkinRender.Image`, `.Vulkan*`, `.Silk`,
  `.MojangApi`, `.SkinDownload` and `.Test` are **not** vendored and are not referenced.

Assembly name and namespaces stay `MinecraftSkinRender` / `MinecraftSkinRender.OpenGL`, so
`Controls/SkinRenderControl.cs` and `Controls/OpenGL/AvaloniaApi.cs` needed no import changes.

## Local changes from upstream

`SkinRender.cs` — `GetMatrix4` restructured, and the single mirrored pair of pose inputs replaced:

- `ArmRotate` / `LegRotate` (one vector driving both sides, negated for the right limb — so only
  an antiphase walk swing was reachable) became `LeftArmRotate` / `RightArmRotate` /
  `LeftLegRotate` / `RightLegRotate`, plus `BodyRotate`. The mirrored `_skina` walk-animation path
  is unchanged.
- New per-part offsets `BodyPos` / `HeadPos` / `LeftArmPos` / `RightArmPos` / `LeftLegPos` /
  `RightLegPos`, applied in root space before each part's pivot. A crouch folds the rig (head
  sinks toward the torso, feet slide back) instead of tipping the whole model over, and a rigid
  global transform cannot produce that.
- Limb pivots moved to where Minecraft puts them — head about the neck joint (was 2px below it),
  arms about the shoulder top (upstream pivoted them at the hand, so any arm rotation swung the
  shoulder), legs about the top-centre of the hip. Rest placements are unchanged, so a model with
  no pose set renders exactly as upstream.

`OpenGL/SkinRenderOpenGL.cs` — `DrawSkin()` uploaded a hardcoded `Matrix4x4.Identity` for the base
torso while `DrawSkinTop()` used `GetMatrix4(ModelPartType.Body)`; with a posed torso the jacket
layer would have torn away from the body, so both now use the same matrix.

## Split into two projects (local change)

The vendored copy is now **two** projects, so the WPF side can reuse the pose maths without
dragging in `libSkiaSharp`:

- **`libs/MinecraftSkinRender.Core/`** — `RootNamespace` is still `MinecraftSkinRender`,
  **zero `PackageReference`**. Holds `CubeModel`, `Enums`, `SkinAnimation`, `SkinModelObj`,
  `Steve3DModel`, `Steve3DTexture`, `SkinRenderVersion`, plus a new `SkinRenderBase` carrying
  everything that does not touch Skia: canvas size, skin type, back colour, mouse interaction,
  pose maths, animation driving.
- **`libs/MinecraftSkinRender/`** (this directory, Skia side) — only `SkinRender : SkinRenderBase`
  (the `_skinTex` / `_cape` fields and `SetSkinTex` / `SetCapeTex`), `SkinTypeChecker`, `OpenGL/*`.
  It `ProjectReference`s the Core project.

Why split rather than duplicate: the `GetMatrix4` changes above are the whole point of vendoring,
and two copies of them would drift visually. `SkinRenderBase` is the single source.

`NegiCraftLauncher.Raster` references **only** `MinecraftSkinRender.Core` and inherits
`SkinRenderBase` (see `SkinRenderSoftware.cs`) — that is why `libSkiaSharp.dll` stays out of the
WPF build. It also ships its own hand-written PNG decoder (`PngCodec`) for the same reason.

## Local addition: two-segment limbs (elbow / knee)

`Steve3DModel` / `Steve3DTexture` / `SkinRenderBase` gained an **opt-in** jointed mode, because a
single rigid box per limb cannot express "hands on hips" — the forearm has nowhere to fold.

- `GetSteve(type, jointed)` / `GetSteveTop(type, jointed)` and the matching texture methods split
  each arm and leg into an upper and a lower segment (`LeftArm` becomes the upper one; `LeftForeArm`
  / `LeftLowerLeg` etc. are new). **The `jointed: false` overloads return exactly what they returned
  before**, and that is what the OpenGL backend keeps calling — it builds one VAO per hand-named
  part, so feeding it a split model would silently draw only the upper halves.
- Segments are authored with **their own joint at the local origin**, which is what lets `Chain()`
  hang a child off its parent. Both layers keep the joints at the same world height, so the 1.125x
  overlay only changes box extents.
- Segments overlap by `Steve3DModel.JointOverlap` (1 skin pixel) across the joint, and the cut faces
  sample a real sleeve row — so a bent limb reads as one continuous piece instead of two sticks.
  At zero pose the two segments reproduce the original box and the render is pixel-identical
  (measured 0.09% on the pet, under the ~3.7% idle-breathing noise floor).
- `SkinRenderBase.LimbJoints` is the switch. Its setter raises `_switchModel`, **not** `_switchType`
  — the software rasterizer only rebuilds its layer list on `_switchModel`, and backends with their
  own mesh cache override `OnLimbJointsChanged()`.

## Local addition: pelvis and chest joints

The rig was completely flat — every part got its own matrix and nothing inherited `BodyRotate`, so
poses like the crouch had to hand-copy the torso's lean into each limb's angle. A hip sway needs a
real chain ("hips turn, chest counter-turns, head stays on target"), which copying cannot express.

- Two new inputs, `HipRotate` / `HipPos` (pelvis) and `SpineRotate` (chest). Mounting:
  pelvis → both legs and the chest; chest → torso box, head and both arms.
- **Both are the identity matrix at zero** (`T(-pivot) · I · T(pivot)` cancels), so every pose that
  predates this — crouch, walk, attack, dangle — renders exactly as before. Only the new sway
  animation drives them. Verified: pet at rest, 0.15% pixel delta with the joints toggled, against
  a ~3.7% idle-breathing noise floor.
- Consequence for future poses: prefer these over `BodyRotate`. `BodyRotate` still only turns the
  torso box, which is now a visible lie if you rotate it by more than a few degrees.

## Re-syncing with upstream

This is a fork; upstream updates are a **manual** merge — diff the new upstream
`MinecraftSkinRender` and `MinecraftSkinRender.OpenGL` against these two directories and
re-apply the edits above. Keep the Core/Skia split in mind: new upstream code that touches
`SKBitmap` belongs in the Skia side, everything else in Core.
