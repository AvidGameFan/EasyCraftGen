# EasyCraftGen

EasyCraftGen is a C# / Avalonia companion window for PhotoCraft on Windows and Linux. It sends a selection to a running Easy Diffusion server for inpainting, then pastes the result into PhotoCraft as a new layer masked by the selection. It can also use Easy Diffusion's Real-ESRGAN filter to open an upscaled copy as a new document. It does not use Python or ComfyUI itself; Easy Diffusion is a separate application and has its own dependencies.

## Requirements

- PhotoCraft 0.20 or newer with its local control channel enabled and **Preferences > Integrations > Allow Agent Control** enabled.
- Easy Diffusion running (default `http://127.0.0.1:9000`), with a compatible inpainting model and Real-ESRGAN model installed. Model names in the window must match the server's installed models. Model support and results depend on the Easy Diffusion backend.
- For running from source, .NET SDK 10.

## Connect PhotoCraft

PhotoCraft and EasyCraftGen must use the **same** control port, token file and exchange directory. The exchange directory is PhotoCraft's automation read and write root. Configure those paths in EasyCraftGen, then launch PhotoCraft with its control flags, for example (replace the paths with absolute paths appropriate to your machine):

```text
photocraft --control 7878 --control-token-file <absolute-token-file> --automation-read-root <absolute-exchange-directory> --automation-write-root <absolute-exchange-directory>
```

Create the exchange directory before launching PhotoCraft. PhotoCraft creates the token file if needed. Protect the token file and exchange directory from other users; on Windows use a user-only ACL, and on Linux use a private directory. PhotoCraft also supports `PHOTOCRAFT_CONTROL_PORT`, `PHOTOCRAFT_CONTROL_TOKEN_FILE`, `PHOTOCRAFT_AUTOMATION_READ_ROOT`, and `PHOTOCRAFT_AUTOMATION_WRITE_ROOT` environment variables. Restart PhotoCraft after changing them. The app deliberately does not alter existing system environment settings.

Use **Test connections** and **Save settings** in the window. Settings are saved in the OS application-data directory under `EasyCraftGen`. The token itself is not saved in settings. The Easy Diffusion URL can point to another machine; document pixels and prompts are sent to that server, so only use a trusted connection.

**Test connections** checks the control connection and Easy Diffusion, not the exchange directory. If PhotoCraft exports a PNG but EasyCraftGen reports it missing, check that the exchange directory in EasyCraftGen is exactly the same directory supplied to both PhotoCraft automation root options, not a parent or child directory. Restart PhotoCraft if its launch options or environment variables change.

## Use

1. Open a document in PhotoCraft and select the area to replace or extend.
2. Enter a prompt and select a compatible diffusion model in EasyCraftGen, then choose **Generate fill**.
3. Keep the document and selection unchanged until the result is placed. The pasted layer is masked by PhotoCraft's actual selection. PhotoCraft's clipboard is used during placement and its contents are replaced. If placement fails, the status reports where the PNG was saved and any partial PhotoCraft changes may be undone.
4. For upscale, choose a Real-ESRGAN model and 2x or 4x. The output opens as a new document, leaving the original untouched, and remains in the exchange directory.

EasyCraftGen loads the model lists from Easy Diffusion when it opens. Use **Refresh model lists** after changing the server URL or installing models. Type a model name or use the arrow beside its field to choose a listed diffusion model, VAE, or upscaler. The server may include suggested models that are not installed yet. Add multiple text encoders with the arrow beside the encoder field, or enter one per line in the required order; leave the list empty to use the model's default. The optional VAE field can also be left empty. Sampler defaults to Euler and scheduler to Simple; available combinations depend on the Easy Diffusion backend and model. Save settings to retain your choices.

The generation mask is the selection's *bounding rectangle*, not its precise shape; complex or disjoint selections may therefore influence the model beyond the selected area. The final layer uses the actual PhotoCraft selection. Inpainting is performed at approximately one megapixel and scaled to the original document size; transparent areas are initialized as white. This first version does not implement seam color matching, tiled generation, or generative upscale. Images above 8192 pixels on either side after upscaling are rejected. The document image and selection bounds are checked before placement, but a change to the selection shape with identical bounds cannot be detected. Do not edit or switch documents while an operation is being placed.

Cancellation stops this app's wait, not necessarily a job already queued on Easy Diffusion. Avoid submitting sensitive images to untrusted servers.

## Build and package

```text
dotnet build EasyCraftGen.sln -c Release
dotnet run --project EasyCraftGen
dotnet test EasyCraftGen.sln -c Release
```

On Windows, run `run-build-windows.bat`, `run-build-windows-arm64.bat`, or `run-build-linux.bat`, optionally passing a version. Packages are self-contained and written to `dist`. For a cross-built Linux zip, run `chmod +x EasyCraftGen` after extraction. On Linux, `sh build-linux.sh [version] [linux-x64|linux-arm64]` produces a tarball preserving executable permissions. Packaging requires the appropriate .NET runtime packs to be available.

Based on the PhotoCraft control protocol, [photocraft-genfill](https://github.com/JohnyDaFace/photocraft-genfill) workflow, and the local EDMCP and PhotoComp projects.
