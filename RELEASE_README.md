# StarSim Core — Release Guide / Ghid de utilizare

Official releases are linked from https://starsim.ro/. Verify release checksums before running an installer obtained from another location. Forks, repackaged installers, modified binaries and third-party plugins are the responsibility of their distributors. See `LICENSE`, `NOTICE`, `SECURITY.md` and `THIRD_PARTY_NOTICES.md` included with this release.

## Română

### Pornire rapidă

1. Extrageți întregul ZIP într-un folder cu drept de scriere. Nu rulați aplicația direct din arhivă.
2. Porniți `StarSimCore.App.exe`.
3. Folosiți **Deschide** pentru o imagine TIFF, PNG sau JPEG deja stivuită.
4. În Beginner alegeți planeta/ținta și un preset, apoi reglați intensitatea. În Expert puteți controla separat fiecare procesor.
5. Activați **Comparare** pentru separatorul Original/Procesat. Click dreapta pe imagine deschide meniul rapid Undo/Redo, Comparare, Potrivire, Reset și Export.
6. Folosiți **Export** pentru rezultatul final la rezoluție completă. Imaginea sursă nu este modificată.
7. Pentru mai multe imagini, puteți porni **Procesare multiplă** (`Ctrl+Shift+B`) fără o imagine deschisă: alegeți folderul, editați prima imagine încărcată, apoi apăsați **Aplică pe tot batch-ul**. Dacă imaginea de referință este deja editată, selectarea folderului pornește automat procesarea secvențială în subfolderul `StarSim_Batch`.

Limba se schimbă din **Settings → Language / Setări → Limbă**. Ghidul integrat se deschide din **Help → User Guide** sau `Ctrl+F1` și explică fluxurile Beginner/Expert, ordinea pipeline-ului și fiecare modul.

Documentația tehnică și practică livrată cu aplicația se află în folderul `docs/`: `MODULE_GUIDE_RO.md` explică funcția și folosirea fiecărui modul non-Wavelet, `PRESET_ENGINE_V1.md` descrie exact maparea Beginner, `BATCH_PROCESSING.md` explică procesarea multiplă, iar `PROCESSING_ENGINE_MATH.md` definește formulele motorului.

### Pluginuri

Deschideți **Plugins → Plugin Manager → Install plugin…**, selectați un DLL Windows x64 de încredere și reporniți aplicația. Procesoarele acceptate apar în Expert, grupul `PLUGINS`, cu panou generat automat și opțiune de fereastră separată.

Pluginurile native rulează în proces și nu sunt izolate. Nu instalați DLL-uri din surse necunoscute. Exemplul inclus oferă Invert/Tint și conversie RGB în alb-negru. Pentru dezvoltare, deschideți folderul `sdk/`, citiți `sdk/README.md` și tutorialul complet `sdk/PLUGIN_SDK.md`.

### Fișiere utile

- proiectele StarSim Core folosesc extensia `.starsim`;
- `CHANGELOG.md` enumeră modificările versiunii și lucrările pregătite pentru următoarea versiune;
- logurile de diagnostic se găsesc în folderul `logs` al aplicației;
- pluginurile utilizatorului se instalează în `%AppData%\StarSimCore\plugins`;
- eliminarea unui plugin din manager îl arhivează recuperabil și necesită repornire.

---

## English

### Quick start

1. Extract the complete ZIP to a writable directory. Do not run the application from inside the archive.
2. Start `StarSimCore.App.exe`.
3. Use **Open** to load an already-stacked TIFF, PNG or JPEG image.
4. In Beginner, choose the target and a preset, then adjust strength. Expert exposes every processor independently.
5. Enable **Compare** to show the Original/Processed divider. Right-click the image for quick Undo/Redo, Compare, Fit, Reset and Export actions.
6. Use **Export** for the final full-resolution result. The source image is never modified.
7. To process multiple images, you can start **Batch Processing** (`Ctrl+Shift+B`) with no image open: choose the folder, edit the first loaded image, then select **Apply to Entire Batch**. If a reference image is already edited, selecting the folder starts sequential processing automatically in the `StarSim_Batch` subfolder.

Change language from **Settings → Language**. Open **Help → User Guide** or press `Ctrl+F1` for the built-in Beginner/Expert workflow, canonical pipeline order and module-by-module guide.

The packaged `docs/` folder contains `MODULE_GUIDE_EN.md`, a practical guide to every non-Wavelet module, `PRESET_ENGINE_V1.md` for the exact Beginner mapping, `BATCH_PROCESSING.md` for the Batch workflow, and `PROCESSING_ENGINE_MATH.md`, the native engine formula reference.

### Plugins

Open **Plugins → Plugin Manager → Install plugin…**, select a trusted Windows x64 DLL and restart the application. Accepted processors appear in Expert under `PLUGINS`, with automatically generated inline controls and a separate movable panel.

Native plugins run in-process and are not sandboxed. Do not install unknown DLLs. The bundled sample provides Invert/Tint and RGB-to-B&W processors. To develop a plugin, open `sdk/`, read `sdk/README.md`, then follow the complete `sdk/PLUGIN_SDK.md` tutorial.

### Useful locations

- StarSim Core projects use the `.starsim` extension;
- `CHANGELOG.md` lists the release changes and work prepared for the next version;
- diagnostic logs are stored in the application's `logs` directory;
- user plugins are installed in `%AppData%\StarSimCore\plugins`;
- removing a plugin in the manager archives it recoverably and requires restart.
