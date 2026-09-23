# Ghidul modulelor non-Wavelet

Data actualizării: 2026-09-22

Acest document descrie funcțiile native, parametrii și folosirea practică a tuturor modulelor de procesare, cu excepția motorului Wavelet. Wavelet este calibrat separat și nu a fost modificat de actualizările descrise aici. Formulele complete și ordinea canonică se află în [PROCESSING_ENGINE_MATH.md](PROCESSING_ENGINE_MATH.md).

## Reguli generale

- Procesarea internă este `float32`; valorile nu sunt tăiate automat la `[0,1]`. Viewerul și exportul întreg fac conversia finală.
- Un modul dezactivat este bypass exact. La Unsharp Mask, Multi-scale Sharpen, Noise Reduction, Richardson–Lucy, Deringing și Local Detail, simpla activare poate rămâne neutră deoarece parametrul principal pornește la zero.
- Evaluați claritatea la zoom 100%. La zoom mic, redimensionarea imaginii poate ascunde zgomot, ringing sau muchii duble.
- Folosiți o singură metodă principală de accentuare. Wavelet + Unsharp + Multi-scale + Richardson–Lucy la valori mari nu înseamnă automat mai mult detaliu real.
- Ordinea este: RGB Align → Noise Reduction → Richardson–Lucy → Wavelet → Unsharp → Multi-scale → Deringing → Local Detail → Color → Tone.

## RGB Align

**Funcție:** aliniază canalele roșu și albastru față de verde prin phase correlation FFT și resampling Lanczos-3 subpixel.

**Controale:** Auto caută deplasarea în limita Search Radius. Red/Green/Blue X/Y adaugă deplasări manuale.

**Folosire:** activați înainte de sharpening dacă limbul are franjuri roșii/albastre. Încercați Auto cu Search Radius 2–6; corectați manual numai reziduuri mici. O rază de căutare mare poate alege o structură greșită pe imagini foarte zgomotoase.

## Noise Reduction

**Funcție:** descompunere Gaussiană pe trei benzi, estimare robustă MAD a zgomotului și soft-threshold separat pentru luminanță și crominanță. După filtrarea culorii, componenta reziduală de luminanță este eliminată din planele cromatice.

**Controale:** Luminanță reduce granulația de luminozitate; Crominanță reduce petele colorate; Fine Detail Protection păstrează coeficienții puternici; Threshold multiplică nivelul estimat al zgomotului.

**Pornire recomandată:** Luminanță `0,10–0,30`, Crominanță `0,20–0,50`, Protection `0,65–0,85`, Threshold `0,8–1,5`. Acestea sunt intervale conservative, nu presetări universale. Dacă ambele intensități sunt zero, rezultatul este identic cu sursa.

## Richardson–Lucy

**Funcție:** deconvoluție iterativă cu PSF Gaussian normalizat și estimare nenegativă. Strength amestecă restaurarea cu sursa; Damping micșorează fiecare actualizare iterativă.

**Controale:** Iterations stabilește numărul de actualizări; PSF Radius aproximează blurul; Strength controlează cât din rezultat intră în imagine; Damping stabilizează actualizările.

**Pornire recomandată:** 3–8 iterații, PSF `0,7–1,5`, Strength `0,10–0,35`, Damping `0,02–0,20`. Opriți când apar granulație, margini duble sau halo. Damping nu este modul anti-halo; Deringing rămâne separat.

## Unsharp Mask

**Funcție:** calculează diferența dintre imagine și un blur Gaussian, aplică soft-threshold și adaugă detaliul înapoi.

`Output = Input + Amount · soft(Input - Gaussian(Input, Radius), Threshold)`

**Pornire recomandată:** Amount `0,05–0,35`, Radius `0,5–1,5`, Threshold `0–0,02` pentru date curate. Măriți Threshold dacă este accentuat zgomotul. Razele și cantitățile mari produc ușor halo la limbul planetar.

## Multi-scale Sharpen

**Funcție:** formează două benzi complementare. Fine Radius definește separarea detaliului fin; Broad Radius este raza Gaussiană țintă efectivă a structurii largi, nu încă un blur adăugat peste prima rază.

`Dfine = Input - Gaussian(Input, FineRadius)`

`Dbroad = Gaussian(Input, FineRadius) - Gaussian(Input, BroadRadius)`

Motorul păstrează automat Broad Radius cu cel puțin `0,15` peste Fine Radius dacă o presetare veche inversează scările.

**Pornire recomandată:** Fine Amount `0,05–0,30`, Fine Radius `0,5–1,2`, Broad Amount `0,02–0,15`, Broad Radius `2–5`, Threshold `0–0,02`. Folosiți-l ca metodă principală sau foarte discret după Wavelet.

## Deringing / Halo Protection

**Funcție:** limitează depășirile locale față de anvelopa vecinilor. Pentru RGB, decizia este calculată o singură dată din luminanță, apoi aceeași corecție este aplicată canalelor R/G/B, reducând riscul de margini colorate. Razele fracționare interpolează între două anvelope, deci sliderul nu mai lucrează în trepte întregi.

**Controale:** Strength este proporția corecției; Radius aproximează lățimea artefactului; Edge Protection extinde anvelopa permisă — o valoare mai mare păstrează mai mult contrast și corectează mai puțin.

**Pornire recomandată:** Strength `0,10–0,35`, Radius `1,5–3,5`, Edge Protection `0,60–0,85`. Verificați limbul și umbrele inelelor lui Saturn la 100%. Modulul corectează extreme locale; nu poate recunoaște perfect orice halo larg și nu trebuie folosit pentru a crea detaliu.

## Local Detail

**Funcție:** adaugă două diferențe Gaussiene — o scară locală și una micro. Edge Protection folosește răspunsul maxim al ambelor benzi, protejând și muchiile foarte fine.

**Pornire recomandată:** Local Amount `0,05–0,25`, Local Radius `4–12`, Micro Amount `0,02–0,15`, Micro Radius `0,6–1,5`, Edge Protection `0,70–0,90`. Este util pentru benzi planetare și relief lunar, dar poate face fundalul pătat.

## Advanced Color, RGB Balance și Saturation

- **Advanced Color:** White R/G/B corectează balansul de alb; Temperature deplasează cald/rece; Tint deplasează verde/magenta; Vibrance favorizează culorile inițial slabe.
- **RGB Balance:** multiplică direct fiecare canal. Valoarea neutră este `1`.
- **Saturation:** multiplică distanța canalelor față de luminanța Rec.709. Valoarea neutră este `1`.

Corectați întâi balansul și abia apoi saturația. Verificați franjurile de la margini; saturarea puternică amplifică și zgomotul cromatic.

## Advanced Tone, Exposure, Contrast și Gamma

- **Advanced Tone:** normalizează Black/White Point, adaugă Brightness și modelează Shadows/Highlights. Ponderile umbrelor/luminilor sunt limitate în `[0,1]`, astfel încât valorile float deja sub negru sau peste alb nu produc creșteri pătratice necontrolate.
- **Exposure:** `Output = Input · 2^Stops`.
- **Contrast:** `Output = 0,5 + Factor · (Input - 0,5)`; neutru `1`.
- **Gamma:** `Output = sign(Input) · |Input|^(1/Gamma)`; neutru `1`.

Ordine practică: stabiliți Black/White Point, corectați expunerea generală, ajustați Gamma pentru tonurile medii, apoi aplicați un contrast final mic. Urmăriți histograma și exportul, nu doar viewerul.

## Ce înseamnă „funcționează”

Fiecare modul de mai sus are implementare nativă reală și parametri conectați direct la pipeline. Acest lucru nu garantează că orice valoare produce un rezultat bun sau că un algoritm simplu poate identifica semantic toate artefactele. Calitatea finală trebuie confirmată pe imagini planetare reale, iar intervalele din acest ghid sunt puncte de pornire prudente.
