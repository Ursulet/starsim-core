# Batch Processing / Procesare multiplă

## English

Batch Processing applies one fixed canonical `PipelineSnapshot` to a deterministic list of images. It supports two entry flows. In both flows the snapshot is copied immediately before processing starts; subsequent slider, preset or Beginner/Expert changes in the main window do not alter that session. The progress window displays its revision and SHA-256 pipeline hash.

### Workflow

**Flow A — choose the batch first:**

1. With no image open, choose **Batch Processing** or press `Ctrl+Shift+B`, then choose the source folder.
2. StarSim Core captures the top-level `.tif`, `.tiff` and `.png` list in deterministic filename order and opens the first image in the normal editor.
3. Edit that reference image in Beginner or Expert mode. The toolbar action becomes **Apply to Entire Batch**.
4. Choose **Apply to Entire Batch**. The current pipeline is frozen and the captured list starts processing automatically.

**Flow B — prepare the reference first:**

1. Open and edit any reference image normally.
2. Choose **Batch Processing**, then select the source folder.
3. The current pipeline is frozen and all supported images from the selected folder start processing automatically.

The default destination is `StarSim_Batch` inside the source folder. The current application export format is reused (16-bit TIFF by default). The Batch window reports per-file and overall progress and provides safe cancellation. Existing outputs are skipped unless overwrite is explicitly enabled.

Outputs use `<original-name>_batch_<index>` with a four-digit index, for example `saturn_batch_0001.tif`. The index belongs to the stable sorted input list, so skipped or failed items do not renumber later outputs.

### Processing and safety guarantees

- Processing is strictly sequential. Only one source document, one native pipeline and one output image are active for the Batch session at a time.
- Every file uses `ProcessingQuality.FullResolution` and resolution scale `1.0`; the interactive preview path and histogram rendering are not used.
- The shared `ResourceGovernor` performs full-resolution memory admission before native processing and coordinates Batch work with other application jobs.
- Each file gets a fresh headless export pipeline. Its native cache, source document and output are disposed before the next file.
- Output is encoded to a uniquely named hidden temporary file. It is moved to the final name only after successful completion. Cancellation or failure removes the temporary file where possible.
- A preflight check refuses a naming plan that could overwrite any selected source image.
- One failed or incompatible image is recorded and the batch continues. A user cancellation stops the entire remaining queue.
- Built-in and enabled plugin processors use the exact states stored in the frozen snapshot. Processor validation is performed independently for each image, so mixed grayscale/RGB or incompatible dimensions may produce per-file failures.

## Română

Procesarea multiplă aplică același `PipelineSnapshot` canonic și fix unei liste deterministe de imagini. Sunt disponibile două fluxuri de pornire. În ambele cazuri, snapshot-ul este copiat imediat înainte de pornirea procesării; modificările ulterioare ale sliderelor, preseturilor sau modului Beginner/Expert din fereastra principală nu schimbă sesiunea. Fereastra de progres afișează revizia și hash-ul SHA-256 al pipeline-ului.

### Flux de utilizare

**Fluxul A — alegi mai întâi batch-ul:**

1. Fără imagine deschisă, alege **Procesare multiplă** sau apasă `Ctrl+Shift+B`, apoi selectează folderul sursă.
2. StarSim Core capturează lista fișierelor `.tif`, `.tiff` și `.png` din nivelul principal, în ordine deterministă după nume, și deschide prima imagine în editorul normal.
3. Editează imaginea de referință în modul Începător sau Expert. Acțiunea din bara principală devine **Aplică pe tot batch-ul**.
4. Apasă **Aplică pe tot batch-ul**. Pipeline-ul curent este blocat, iar lista capturată începe să fie procesată automat.

**Fluxul B — pregătești mai întâi referința:**

1. Deschide și editează normal orice imagine de referință.
2. Alege **Procesare multiplă**, apoi selectează folderul sursă.
3. Pipeline-ul curent este blocat, iar toate imaginile acceptate din folderul ales încep să fie procesate automat.

Destinația implicită este subfolderul `StarSim_Batch` din folderul sursă. Este refolosit formatul de export curent al aplicației (implicit TIFF 16 biți). Fereastra Batch afișează progresul total și pe fișier și permite anularea sigură. Rezultatele existente sunt sărite dacă suprascrierea nu este activată explicit.

Rezultatele folosesc forma `<nume-original>_batch_<index>` cu index de patru cifre, de exemplu `saturn_batch_0001.tif`. Indexul aparține listei sursă sortate stabil, deci fișierele sărite sau eșuate nu renumerotează rezultatele următoare.

### Garanții de procesare și siguranță

- Procesarea este strict secvențială. Sesiunea Batch păstrează activă o singură imagine sursă, un singur pipeline nativ și un singur rezultat.
- Fiecare fișier folosește `ProcessingQuality.FullResolution` și scara `1.0`; preview-ul interactiv și calculul histogramei nu sunt folosite.
- `ResourceGovernor` face admiterea de memorie la rezoluție completă și coordonează Batch cu celelalte operații ale aplicației.
- Fiecare fișier primește un pipeline headless nou. Cache-ul nativ, documentul sursă și rezultatul sunt eliberate înainte de fișierul următor.
- Imaginea este codificată întâi într-un fișier temporar ascuns și unic. Acesta este mutat la numele final numai după succes. La anulare sau eroare, fișierul temporar este șters când sistemul permite.
- Verificarea preliminară refuză orice plan de denumire care ar putea suprascrie una dintre sursele selectate.
- O imagine incompatibilă sau eșuată este înregistrată, iar lotul continuă. Anularea utilizatorului oprește întreaga coadă rămasă.
- Procesoarele integrate și pluginurile active folosesc exact stările din snapshot-ul blocat. Validarea rulează separat pentru fiecare imagine; amestecarea imaginilor grayscale/RGB sau a unor dimensiuni incompatibile poate produce erori individuale.
