# EasyCraftGen

EasyCraftGen is a C# / Avalonia companion window for PhotoCraft on Windows and Linux, with an experimental script-based PaintShop Pro integration on Windows. It sends the image to a running Easy Diffusion server for a prompted fill or edit, then returns the result as a new layer. A selection masks the fill; without one, the whole image is edited. In PhotoCraft it can also use Easy Diffusion's Real-ESRGAN filter to open an upscaled copy as a new document. EasyCraftGen itself does not use Python or ComfyUI; the PaintShop Pro integration uses PSP's embedded Python scripting, and Easy Diffusion is a separate application.

## Requirements

- For PhotoCraft: version 0.20 or newer with its local control channel enabled and **Preferences > Integrations > Allow Agent Control** enabled. PaintShop Pro mode does not require PhotoCraft.
- Easy Diffusion running (default `http://127.0.0.1:9000`), with a compatible diffusion model and Real-ESRGAN model installed. Use a model suited to inpainting for selected fills or instruction-based editing for whole-image edits. Model names in the window must match the server's installed models. Model support and results depend on the Easy Diffusion backend; not all models follow edit or removal instructions.
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

1. Open a document in PhotoCraft. Select an area to fill or modify it, or clear the selection to edit the whole image.
2. Enter what to create, change, or remove and select a compatible diffusion model in EasyCraftGen, then choose **Generate fill / edit**. Optionally choose a prompt starter, such as **Remove**, **Enhance**, or **Change the style to**, and complete it in the prompt box. Starters are appended; the prompt remains freely editable.
3. Keep the document and selection unchanged until the result is placed. With a selection, the pasted layer is masked by PhotoCraft's actual selection; otherwise, the edited image is pasted as an unmasked layer. PhotoCraft's clipboard is used during placement and its contents are replaced. If placement fails, the status reports where the PNG was saved and any partial PhotoCraft changes may be undone.
4. For upscale, choose a Real-ESRGAN model and 2x or 4x. The output opens as a new document, leaving the original untouched, and remains in the exchange directory.

EasyCraftGen loads the model lists from Easy Diffusion when it opens. Use **Refresh model lists** after changing the server URL or installing models. Type a model name or use the arrow beside its field to choose a listed diffusion model, VAE, or upscaler. The server may include suggested models that are not installed yet. Add multiple text encoders with the arrow beside the encoder field, or enter one per line in the required order; leave the list empty to use the model's default. The optional VAE field can also be left empty. Sampler defaults to Euler and scheduler to Simple; available combinations depend on the Easy Diffusion backend and model. Save settings to retain your choices.

For a selected fill, the generation mask is the selection's *bounding rectangle*, not its precise shape; complex or disjoint selections may therefore influence the model beyond the selected area. The final layer uses the actual PhotoCraft selection. Without a selection, the entire image is sent without a mask. Under **Advanced**, **Generation resolution** sets an approximate target of 1, 2, 4, or 8 megapixels for the image sent to Easy Diffusion (default 1 MP); higher settings can require substantially more VRAM and time. **Generation pixel step** rounds each dimension to the nearest multiple of 64, 16, or 1 pixel (default 64). The image content is not deliberately enlarged to reach the target, but rounding can change its dimensions. The 1-pixel option disables additional alignment in EasyCraftGen; it does not bypass the megapixel target, and Easy Diffusion or its model may impose its own dimension requirements. For example, current Easy Diffusion versions round dimensions down to multiples of 8. The result is scaled back to its original document coordinates; transparent areas are initialized as white. This first version does not implement seam color matching, tiled generation, or generative upscale. Images above 8192 pixels on either side after upscaling are rejected. The document image and selection bounds are checked before placement, but a change to the selection shape with identical bounds cannot be detected. Do not edit or switch documents while an operation is being placed.

**Optimize selection for low VRAM** (off by default) sends only the selection's bounding rectangle and at least 64 pixels of surrounding image where available. Small crops are expanded with additional image context when possible. The option has no effect without a selection. Every generation request is at least 384 pixels wide and high, even when optimization is off; smaller dimensions are padded with white after resolution scaling rather than stretching the image. Padding can make a request exceed the megapixel target, especially for very narrow images. Padding is removed from the result, and optimized results are positioned at the crop's original document offset before the actual selection is applied as a layer mask. Cropping reduces context, which may affect the result.

Cancellation stops this app's wait, not necessarily a job already queued on Easy Diffusion. Avoid submitting sensitive images to untrusted servers.

## PaintShop Pro integration (experimental)

The separate `EasyCraftGen-PaintShopPro` folder contains `EasyCraftGen.PspScript` and its setup instructions. Edit `EASYCRAFTGEN_EXE` in the installed script to point to a built or packaged `EasyCraftGen.exe`, then run or bind the trusted script in PaintShop Pro. The script exports a temporary composite and launches EasyCraftGen in PaintShop Pro mode; enter the prompt and choose **Generate fill / edit**. On success, the script pastes the full-size result and applies the saved selection as a mask. With no selection it pastes an unmasked layer. Close the EasyCraftGen window to cancel; the **Cancel** button stops an active request and permits retrying or closing.

PaintShop Pro may be blocked while the script waits. Do not edit or switch the original document until it finishes. The selection's bounding rectangle is used for generation and the actual selection is used for the layer mask; selection-bound interpretation, full integration, and feathering need validation in the real host. Optimized crops are reconstructed at full document size before the script pastes them. The script leaves its temporary files and duplicate/result documents for inspection, and reports a saved selection name that may remain in PSP's configured selection folder. Those files can contain private image data; delete them when no longer needed. Multilayer export flattens the duplicate and may lose transparency. Upscaling is not supported in this mode.

## Build and package

```text
dotnet build EasyCraftGen.sln -c Release
dotnet run --project EasyCraftGen
dotnet test EasyCraftGen.sln -c Release
```

On Windows, run `run-build-windows.bat`, `run-build-windows-arm64.bat`, or `run-build-linux.bat`, optionally passing a version. Packages are self-contained and written to `dist`. For a cross-built Linux zip, run `chmod +x EasyCraftGen` after extraction. On Linux, `sh build-linux.sh [version] [linux-x64|linux-arm64]` produces a tarball preserving executable permissions. Packaging requires the appropriate .NET runtime packs to be available.

Based on the PhotoCraft control protocol, [photocraft-genfill](https://github.com/JohnyDaFace/photocraft-genfill) workflow, and the local EDMCP and PhotoComp projects.
