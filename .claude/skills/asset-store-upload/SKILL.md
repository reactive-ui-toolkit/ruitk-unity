---
name: asset-store-upload
description: Upload a new package version to the Unity Asset Store. Use when the user says "upload to the store", "publish the new version to the asset store", "submit the update", or asks what to do with the .unitypackage after publish.yml runs. Covers the StorePublisher project, the delete-before-import rule that makes the omit lists take effect, and the Validator/Uploader path. Does NOT cover publish.yml itself, which is owner-triggered CI.
---

# Uploading a new version to the Unity Asset Store

The store update is a **manual Unity-side step** by design. Unity ships no CLI or
API for it (Asset Store Publishing Tools v12.0.0 is UI-only), so CI builds and
validates the package and a human uploads it.

## Where things are

| | |
|---|---|
| Publisher project | `storePublisherProject` in the gitignored `.ruitk-local.json` — the Unity project holding `Assets/ReactiveUIToolkit`. Unity **6000.2.6f2**; do not upgrade it. |
| The folder that gets uploaded | `Assets/ReactiveUIToolkit` |
| The package file | `ReactiveUIToolkit-<version>.unitypackage`, from the `publish.yml` run's artifacts or the `v<version>` GitHub Release |
| Portal | publisher.unity.com |

The project also contains `Assets/Ruitk/` (a `UITKX_GeneratorTrigger.g.cs` and a
`Resources/` folder the change-watcher generates locally), plus `Scenes/`,
`Readme.asset` and the input actions. **None of that belongs in the package** —
which is why the Uploader's folder must be set explicitly.

## The steps

### 1. Delete the old package folder FIRST

In the **Project window**, right-click `Assets/ReactiveUIToolkit` → **Delete**.

Do it **in Unity, not in Explorer**, so the `.meta` goes with it.

**This is the step that matters, and skipping it is silent.** Importing over the
top does not remove files that no longer exist in the new version — they simply
survive and ship again. Any `pathsToOmitFromDist` / `pathsToOmitFromStore` change
has no effect unless the folder is deleted first, because the omit lists only
control what goes INTO the new package; they cannot remove what is already
sitting in the project.

Proof this is not hypothetical: `ruitkUiBuiler/` (the browser POC at the package
root) shipped in 0.18.1 and was still in the staged project afterwards. It was
excluded from 0.19.0 onward in `config.json` — and that fix only lands if the old
folder is deleted before the import.

### 2. Import the new package

`Assets → Import Package → Custom Package…` → pick the `.unitypackage` →
**All** → **Import**.

It recreates `Assets/ReactiveUIToolkit/` from the CI-built package, so what you
upload is exactly what CI validated.

### 3. Verify before uploading

Three checks, all in `Assets/ReactiveUIToolkit/`:

- `package.json` shows the **new version**
- `Builder/` **is** present (the editor tool — not to be confused with
  `ruitkUiBuiler/`, the POC, whose folder name is a typo of "Builder")
- anything newly added to the omit lists is **gone**

### 4. Validator, then Uploader

1. `Tools → Asset Store → Validator` — runs Unity's guideline checks. Clear it
   before uploading; it is faster than a rejected review.
2. `Tools → Asset Store → Uploader` → log in → select the **package draft** →
   set the folder to **`Assets/ReactiveUIToolkit`** → **Export and Upload**.

The Uploader **re-exports from the project** rather than uploading the
`.unitypackage` you downloaded. That file's job is transport into this project
plus validation and the GitHub Release download — not store input.

### 5. Submit

At publisher.unity.com: set the version, paste release notes from that version's
`CHANGELOG.md` entry, refresh screenshots if the UI changed, **Submit for
review**. Updates review in roughly 2 business days.

## Before you start

- **Tax and payout details must be complete**, or Submit will not go through.
  This paused a previous attempt after the package was already built.
- The publisher project is on 6000.2.6f2 and CI exports with 6000.2.6f1. A patch
  apart is fine; do not upgrade the project to close the gap — the floor is what
  `package.json` declares.

## Out of scope

`publish.yml` is owner-triggered CI and is covered by `RELEASE_OPS.md`. Note that
a **re-run replays the old SHA** — dispatch fresh instead. This skill starts at
"the package is built and downloaded".
