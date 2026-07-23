# MR Compare

**A Mixed-Reality Framework for Spatially Grounded Visual Comparison of 3D Gaussian Splatting and Mesh Reconstructions with the Physical Environment**

![Aligned 3DGS and live VST comparison in mixed reality](docs/alignment-result-3d-slider.png)

MR Compare is a Unity-based mixed reality project for registering reconstructed 3D scenes against a user's current physical environment and comparing them in-headset. It is designed for workflows where a user brings their own reconstructed scene, either as a 3D Gaussian Splatting asset or as a mesh, and aligns it with the real environment captured by Meta Quest.

The project focuses on three tasks:

- capturing or deriving a reference representation of the current Quest environment;
- registering a reconstructed 3DGS or mesh scene to that reference;
- visually comparing the registered reconstruction with the real mixed reality scene.

## Paper

[![arXiv](https://img.shields.io/badge/arXiv-2607.20325-b31b1b.svg)](https://arxiv.org/abs/2607.20325)
[![Project Page](https://img.shields.io/badge/Project-Page-blue.svg)](https://changruizhu96.github.io/work/mr-compare/)

## Quick Start

1. Clone this repository and open it in Unity `6000.0.76f1`.
2. Let Unity restore the packages from `Packages/manifest.json`.
3. Prepare a source scene using one of these recommended routes:
   - **Fast mobile route — [Scaniverse](https://scaniverse.com/):** capture and process a Gaussian splat directly on a phone. This is the easiest way to try MR Compare because it needs no desktop photogrammetry setup and typically produces a source close enough to real-world scale for an initial alignment. The visual fidelity may be lower than a carefully trained desktop 3DGS, but it is the recommended first experience.
   - **Higher-quality desktop route — [RealityScan](https://www.realityscan.com/download):** first [align the source images](https://rshelp.capturingreality.com/en-US/tutorials/quickstart_2.htm), then establish metric scale with [control points and distance constraints](https://rshelp.capturingreality.com/en-US/tutorials/scaling.htm). Export a mesh, or use the aligned cameras/images as the scale-aware input to a desktop 3DGS pipeline.
4. Import the resulting 3DGS asset or mesh into Unity and add it to `Assets/Scenes/Alignment Demo.unity`. For 3DGS, open `Tools > Gaussian Splats > Create GaussianSplatAsset` and set `Quality` to `Very High` before creating the asset. **`Very High` is required because lower-quality position encoding can prevent MR Compare from reading the 3DGS center correctly.**
5. Confirm that the imported source is close to real-world scale. If it is not, correct it manually before relying on `Estimate Scaling`.
6. For 3DGS, use `Gaussian Splat Render` > `Baking & Debug` to remove floaters, skybox points, and unrelated background geometry.
7. On Quest, complete `Settings > Environment Setup > Space Setup` so the default Effect Mesh workflow has a current global mesh.
8. On `AllInOneRegistration`, set `Workflow Mode` to `Align` and `Target Format` to `effectMesh`, then assign the source renderer or mesh and the `Effect Mesh Event Target`. New components already use the remaining tested defaults: `GaussianSplating`, TEASER, GICP, baked source data, and repeated alignment.
9. Run `Alignment Demo.unity` on Quest. The first alignment starts when the source and Effect Mesh target are ready.
10. If the result needs refinement, press `Y` to align again with the retained target data.

### Visual Comparison Controls

After alignment, use the visual comparison modes to inspect the registered reconstruction against the real environment:

1. Press the left controller menu/start button to open or close the comparison UI.
2. Select one of the three comparison modes:
   - **Mini-Window:** move the controller or tracked hand horizontally to move the local comparison window across the scene.
   - **3D Slider:** move the controller or tracked hand horizontally to move the boundary between the reconstruction and the real environment.
   - **Switch-back:** press the right controller `B` button to switch the reconstruction display on or off.

The comparison modes can target either a `GaussianSplatRenderer` or a standard `MeshRenderer`.

## System Overview

![MR Compare workflow](docs/workflow-ai.jpg)

MR Compare converts both the reconstructed source scene and the Quest-side target environment into point cloud representations before registration.

The high-level workflow is:

1. Collect source data with a phone, camera, or desktop reconstruction pipeline.
2. Import the source as either a mesh or 3DGS asset.
3. Extract clean source points from the mesh or 3DGS representation.
4. Collect target points from Quest scene data, a saved Quest point cloud scan, a room mesh, or an effect mesh.
5. Run coarse registration with voxelisation, FPFH features, and TEASER++.
6. Refine the alignment with GICP/VGICP.
7. Save or load the alignment relative to a spatial anchor, room mesh, or effect mesh.
8. Inspect the result in mixed reality with interactive visual comparison modes.

TEASER++ is used as the robust coarse registration stage. It is especially useful when the initial source and target poses are far apart or when the correspondence set contains outliers. GICP/VGICP is then used as the fine registration stage once the two point clouds have a reasonable initial overlap.

## Features

- Unity mixed reality workflow targeting Meta Quest 3.
- Source support for 3D Gaussian Splatting assets and Unity meshes.
- Target support for pre-scanned point clouds, real-time Quest scans, Meta room meshes, and Meta effect meshes.
- Registration pipeline with optional TEASER++ rough alignment and GICP/VGICP refinement.
- Alignment save/load support using spatial anchors, current room mesh references, or current effect mesh references.
- In-headset visual comparison modes for comparing the registered reconstruction against passthrough.
- Quest point cloud scanner with spatial anchor persistence.

## Project Status

This repository is a research/prototype Unity project. It is intended to be opened, inspected, modified, and adapted for scene-specific registration workflows.

User-specific reconstructed scenes are not included in the repository. To use the project, prepare your own 3DGS asset or mesh model and place it in the Unity project as described below.

## Requirements

- Unity `6000.0.76f1`
- Meta Quest 3 or compatible Meta XR device
- Meta Quest Link-based development and testing workflow
- Tested Meta Quest Link versions: `v77`, `v85`, and `v201`
- Meta XR SDK `201.0.0`
- Meta MR Utility Kit `201.0.0`
- OpenXR / Meta OpenXR
- Universal Render Pipeline `17.0.4`
- Windows editor/runtime environment for the native registration plugin
- [Microsoft Visual C++ Redistributable v14, x64](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist?view=msvc-170) for the native registration DLLs

Make sure the Quest headset OS/runtime is compatible with the Meta Quest Link version used on the development PC. Mismatched Quest Link and headset OS versions can cause environment depth, scene, or OpenXR behavior to differ from the tested setup.

Native registration depends on DLLs under:

```text
Assets/Plugins/x86_64/
```

These include PCL-related libraries and `TeaserDll_final.dll`.

## Repository Layout

```text
Assets/Scenes/
  Room Scanner.unity        Quest point cloud scanning scene
  Alignment Demo.unity      Registration and visual comparison scene

Assets/Scripts/Quest Scan/
  Quest 3 point cloud scanning and spatial-anchor-based saving

Assets/Scripts/Registration/
  Source/target loading, TEASER++/GICP/VGICP registration, alignment save/load

Assets/Scripts/Visual Comparison/
  Registered object management and in-headset comparison modes

Assets/Plugins/x86_64/
  Native registration dependencies

Packages/package/
  Modified Unity Gaussian Splatting package
```

## Preparing Your Source Scene

MR Compare does not ship with a ready-to-use reconstructed scene. You should import your own reconstructed environment into the project.

Supported source types:

- **3D Gaussian Splatting**: use `Tools > Gaussian Splats > Create GaussianSplatAsset`, set `Quality` to `Very High`, create the asset, and assign its `GaussianSplatRenderer` to the aligner. Do not use a lower import quality: MR Compare relies on the uncompressed `Very High` position data to read the 3DGS center correctly.
- **Mesh**: import a reconstructed mesh and assign its `MeshFilter` to the aligner.

The current Inspector enum uses the spelling `GaussianSplating`; this refers to the 3D Gaussian Splatting source mode.

### Source Scale Requirement

The most important requirement for 3DGS registration is scale. The 3DGS scene should be reconstructed at real-world scale, or at least be close enough that TEASER++ can estimate a usable scale correction.

The Quick Start lists the recommended Scaniverse and RealityScan routes. For a desktop 3DGS handoff, preserve RealityScan's calibrated camera poses and metric scale when moving the images into a dedicated generator such as LichtFeld Studio, Nerfstudio's gsplat workflow, or Postshot.

If the imported 3DGS scale is inaccurate, first resize the source to a roughly correct scale in Unity. Then enable TEASER++ `Estimate Scaling`. It can recover a moderate residual scale error, but it should not replace metric source preparation.

### 3DGS Point Cloud Preprocessing

Before matching a 3DGS source against the Quest target, clean the source points used for registration. The goal is to extract a stable, mostly foreground point cloud from the 3DGS asset, instead of registering against floaters, skyboxes, background geometry, or low-quality splats.

Use the `Baking & Debug` section on the `Gaussian Splat Render` component.

Important settings:

- `Max Flatness`: controls filtering based on splat flatness. The `Auto` option works for most scenes and is the recommended starting point.
- `Density Filter`: reduces isolated outlier noise and helps keep the matching point cloud stable.
- `Enable Selection ROI`: restricts the baked/matching points to a region of interest. Use this to remove unwanted background, skybox content, or unrelated geometry captured in the 3DGS.

Recommended preprocessing steps:

1. Select the 3DGS object in Unity.
2. Open the `Gaussian Splat Render` component.
3. Expand `Baking & Debug`.
4. Set `Max Flatness` to `Auto` for the first attempt.
5. Enable the density filter if the 3DGS contains floaters or sparse outliers.
6. Enable selection ROI if the reconstruction includes background, skybox, or irrelevant regions.
7. Bake or update the registration point representation.
8. Assign the cleaned 3DGS source to `AllInOneRegistration`.

If registration fails, inspect the source and target point clouds first. In most failed cases, the cause is one of the following:

- the source scale is not close to real-world scale;
- the source includes too much background or skybox;
- the source and target point clouds do not share enough overlap;
- the source point cloud is too noisy for stable FPFH correspondences;
- the TEASER++ voxel size or GICP/VGICP parameters are not appropriate for the scene scale.

## Choosing a Registration Target

The `AllInOneRegistration` component supports several target modes.

`preScan` and `realTimeScan` use the same underlying target-generation and registration path: the Quest Environment Depth scanner produces a point cloud, which is then aligned with the reconstructed source through the same TEASER/GICP pipeline. The difference is how the workflow is organized. `preScan` saves the scan and anchor for a later alignment session, while `realTimeScan` passes the scanner points directly to alignment in the same scene and session.

When `Allow Repeated Alignment` is enabled, pressing `Y` after the initial alignment reruns registration with the retained target data in `preScan`, `roomMesh`, and `effectMesh` modes. In `realTimeScan`, X can add scan coverage before Y runs the next alignment. Repeated alignment refreshes the source point cloud in its current pose, so each pass acts as an incremental refinement.

New `AllInOneRegistration` components use the tested default profile: `Align`, `effectMesh`, `GaussianSplating`, TEASER rough alignment, GICP refinement, baked source data, repeated alignment enabled, and automatic alignment saving disabled. The numeric TEASER/GICP and selection defaults match `Alignment Demo.unity`. The included demo scene already contains the required Quest scanner and Meta scene references; users only need to import and assign their own reconstruction source. Custom scenes must provide the references required by their selected target mode.

| Target format | Recommended use | Target data | Main tradeoff |
| --- | --- | --- | --- |
| `effectMesh` | Fastest setup and first-time use | Quest Space Setup global Effect Mesh | Fewest operations, but target detail and accuracy are limited by the Effect Mesh |
| `preScan` | Separated, reusable scan/alignment workflow | Environment Depth scanner point cloud loaded with its saved anchor data | Requires a separate scan/save/load stage |
| `realTimeScan` | Integrated scan/alignment workflow | The same Environment Depth scanner point cloud passed directly from memory | Fewer handoff steps; scanner and 3DGS rendering must be isolated at runtime |
| `roomMesh` (legacy) | Compatibility with legacy MRUK scenes | Legacy Room Mesh Event | Retained for existing setups; prefer `effectMesh` for new scenes |

## Detailed Usage

### Simplest Workflow: Effect Mesh

Use this workflow when you want the fewest setup operations and are comfortable using the Quest's existing scene mesh as the registration reference. It is convenient, but it is not the highest-quality option: registration accuracy is constrained by the accuracy, coverage, and geometric detail of the current effect mesh.

1. Scan your physical room on Quest through `Settings > Environment Setup > Space Setup`.
2. Open `Assets/Scenes/Alignment Demo.unity`.
3. Add or enable your reconstructed source object in the scene.
4. Select the object that contains `AllInOneRegistration`.
5. Set `Workflow Mode` to `Align`.
6. Confirm that the source 3DGS or mesh is close to real-world scale.
7. If using 3DGS, preprocess the registration points with `Baking & Debug`.
8. Set `Target Format` to `effectMesh`.
9. Assign the `Effect Mesh Event Target`.
10. Make sure the effect mesh configuration includes the global mesh.
11. Set `Source Type` to either `GaussianSplating` or `Mesh`.
12. Assign the matching source reference.
13. Configure the selection region if you only want to align part of the source model.
14. Choose registration parameters:
    - use `Teaser` when the initial offset is large;
    - use GICP/VGICP refinement when the source and target already have reasonable overlap.
15. Enable `Is Saving` if you want to reuse the result later.
16. Set `Alignment Reference Mode` to `EffectMesh`.
17. Run the scene and wait for the effect mesh target to load.
18. After registration completes, inspect the registered source with the visual comparison tools.

This workflow avoids a separate Environment Depth scan, which makes it the quickest route to registration.

### Separated Workflow: Pre-Scan / Quest 3 Scanner

This workflow uses the same Environment Depth point-cloud target and registration pipeline as `realTimeScan`, but separates scanning from alignment. The Room Scanner saves the points and spatial anchor first, and the Alignment Demo loads them later. This makes the target reusable and keeps scanner collection separate from 3DGS rendering and alignment. Registration quality depends on scan coverage and settings rather than on choosing `preScan` instead of `realTimeScan`.

1. Open `Assets/Scenes/Room Scanner.unity`.
2. Run the scene on Quest.
3. Hold `X` to scan the physical environment.
4. Press `B` to create a spatial anchor and save the scanned point cloud.
5. Open `Assets/Scenes/Alignment Demo.unity`.
6. Set `Workflow Mode` to `Align`.
7. Set `Target Format` to `preScan`.
8. Assign the saved point cloud file name and anchor UUID file name.
9. Assign the anchor holder.
10. Configure the source type and source object.
11. Use `SpatialAnchor` as the alignment reference mode if you want the result to persist relative to the saved scan anchor.
12. Run registration.

The scanner stores:

- point cloud data;
- spatial anchor UUID data.

### Real-Time Scan Workflow

This workflow integrates the same Environment Depth scanner and point-cloud registration path used by `preScan` into one scene and session. Instead of saving and reloading the target, it passes the accumulated scanner points directly from memory to alignment. MR Compare suspends 3DGS rendering while scanning and deactivates the scanner before alignment. Prefer **Pre-Scan / Quest 3 Scanner** when you want a reusable saved target or separate scan/alignment stages; use **Effect Mesh** when minimizing setup operations matters more than target fidelity.

1. Open `Assets/Scenes/Alignment Demo.unity`.
2. Set `Target Format` to `realTimeScan`.
3. Enable `Allow Repeated Alignment` if the user should be able to refine the result with additional X/Y cycles.
4. The included demo scene already has its in-scene Quest scanner assigned to `Scanner Target`. No scanner setup is required unless you are building a custom scene.
5. Run the scene on Quest.
6. Press or hold `X` to start scanning, depending on the active scanner setup.
7. Press `Y` to extract scanner points and trigger registration.
8. If the alignment is not satisfactory, press `X` again. 3DGS rendering is suspended before the embedded scanner is reactivated, and new scan points are added to the retained scan. Press `Y` again to align against the updated target.

## Registration Settings Guide

### TEASER++ Coarse Registration

Enable TEASER++ when the source starts far from the target, the initial rotation is uncertain, or the correspondence set contains outliers. It provides a robust coarse alignment before local refinement.

Tune these parameters first:

- `Voxel Size Teaser`: larger values improve robustness and speed at the cost of detail.
- `Noise Bound Teaser`: increase this for noisier extracted points.
- `Normal Radius Teaser` and `FPFH Radius Teaser`: set these relative to the physical scene scale.
- `Estimate Scaling`: enable only when the source is already reasonably close to metric scale.

### GICP/VGICP Fine Registration

Use GICP/VGICP after coarse alignment, once source and target overlap. VGICP can be more stable for large or noisy point clouds; smaller voxels favour detail, while larger voxels favour speed and robustness.

## Alignment Save/Load Workflow

`AllInOneRegistration` supports two workflow modes:

- `Align`: run registration and optionally save the alignment result.
- `Load`: load a previously saved alignment result and apply it to the source object.

Supported alignment reference modes:

- `SpatialAnchor`: save/load the alignment relative to a spatial anchor.
- `RoomMesh` (legacy): save/load the alignment relative to the legacy room mesh.
- `EffectMesh`: save/load the alignment relative to the current effect mesh. This reference can be used with any registration target format because it is resolved independently from the point cloud or mesh used for registration.

Use `EffectMesh` when you want the alignment to be tied to the Quest's scanned scene mesh rather than to a separately scanned point cloud. The scene must contain an `EffectMeshEvent` configured to provide the global mesh; `Alignment Demo.unity` already includes this setup.

To load a saved alignment:

1. Keep the same source type and source object assignment used during alignment.
2. Set `Workflow Mode` to `Load`.
3. Select the same `Alignment Reference Mode` used when saving.
4. Assign the matching reference: anchor files and holder for `SpatialAnchor`, Room Mesh Event for `RoomMesh`, or Effect Mesh Event for `EffectMesh`.
5. Set the saved alignment file name and run the scene.

The transform is restored relative to the selected reference rather than as a fixed world-space pose.

## Third-Party Components

This project includes modified or adapted work from the following projects:

- 3D Gaussian Splatting support is modified from [aras-p/UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting).
- The Quest 3 scanner workflow is modified from [Appletea0673/Depth-Scanner-Project](https://github.com/Appletea0673/Depth-Scanner-Project).

Additional runtime functionality depends on Meta XR SDK, Meta MR Utility Kit, Unity OpenXR packages, and native registration libraries.

See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for bundled copyright and license notices, including the native TEASER++, PCL, and LZ4 binaries. Package-manager dependencies remain subject to their own package licenses and terms.

## Known Limitations

- The native registration plugin is currently provided as Windows `x86_64` DLLs.
- The repository does not include user-specific reconstructed scenes.
- Large 3DGS files, meshes, point clouds, or native binaries may require Git LFS or external release assets.
- Registration quality depends on overlap, scale consistency, target point-cloud or effect-mesh accuracy, source geometry quality, and parameter tuning.
- The project may require scene-specific setup before it can be used as a turnkey application.

## Citation and Acknowledgements

If MR Compare contributes to an academic publication, please cite the [MR Compare paper](https://arxiv.org/abs/2607.20325):

```bibtex
@misc{zhu2026mrcompare,
      title={MR-Compare: A Mixed-Reality Framework for Spatially Grounded Visual Comparison of 3D Gaussian Splatting and Mesh Reconstructions with the Physical Environment},
      author={Changrui Zhu and Ernst Kruijff and Pengju Zhang and Simon Julier},
      year={2026},
      eprint={2607.20325},
      archivePrefix={arXiv},
      primaryClass={cs.GR},
      url={https://arxiv.org/abs/2607.20325},
}
```

Please also acknowledge the upstream projects listed above and cite the reconstruction, registration, or MR systems used in your workflow. Citation is an academic request and is not an additional condition of the MIT License.

## License

MR Compare is released under the [MIT License](LICENSE). Copyright (c) 2026 Changrui Zhu.

The license applies to the original MR Compare software and accompanying documentation in this repository, except where a file or directory carries a separate license or copyright notice.

Under the MIT License, you may:

- use the software for academic, research, educational, private, or commercial purposes;
- copy, modify, merge, publish, and distribute the software;
- sublicense or sell copies of the software; and
- include the software in open-source or proprietary derivative work.

When redistributing the software or a substantial portion of it, you must retain the original copyright notice and MIT permission notice. The software is provided **as is**, without warranty; see the full [LICENSE](LICENSE) text for the legally controlling terms.

The MIT License does not replace or override licenses for third-party material. In particular:

- the modified Unity Gaussian Splatting package remains subject to its bundled [MIT license](Packages/package/LICENSE.md);
- the adapted [Depth Scanner Project](https://github.com/Appletea0673/Depth-Scanner-Project) is distributed under its upstream MIT license; and
- Meta XR SDK, Meta MR Utility Kit, Unity packages, native registration libraries, and other dependencies remain subject to their respective licenses and terms.

User-supplied datasets, scans, 3DGS assets, meshes, model checkpoints, and other imported content are not covered by the MR Compare license. Users are responsible for ensuring that they have permission to use and redistribute those materials.
