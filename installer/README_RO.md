# Crearea installerului StarSim Core cu Inno Setup 6

Totul este pregătit pentru Windows x64. Installerul include aplicația self-contained (.NET nu trebuie instalat separat), motorul nativ, toate DLL-urile de imagine, presetările, pluginul demonstrativ, SDK-ul de pluginuri, documentația, licența Apache 2.0, notificările componentelor terțe și Microsoft Visual C++ Runtime x64.

## Varianta simplă — o singură comandă

1. Deschideți PowerShell în folderul principal `StarSimCore`.
2. Permiteți scripturile numai pentru această fereastră PowerShell:

   ```powershell
   Set-ExecutionPolicy -Scope Process Bypass
   ```

3. Creați pachetul și installerul:

   ```powershell
   .\scripts\make-installer.ps1
   ```

Scriptul nu rulează testele. El compilează Release, publică versiunea autonomă Windows x64, descarcă Visual C++ Runtime direct de la Microsoft, verifică semnătura Microsoft și pornește compilatorul Inno Setup 6.

Rezultatele apar aici:

```text
artifacts\installer\StarSimCore-Setup-1.0.0-win-x64.exe
artifacts\package\StarSimCore-1.0.0-win-x64-portable.zip
artifacts\installer\StarSimCore-Setup-1.0.0-win-x64-README.md
artifacts\installer\SHA256SUMS.txt
```

## Dacă doriți să apăsați Compile în Inno Setup

1. Pregătiți toate fișierele fără compilarea installerului:

   ```powershell
   .\scripts\make-installer.ps1 -PrepareOnly
   ```

2. Deschideți `installer\StarSimCore.iss` în Inno Setup 6.
3. Apăsați **Build → Compile** sau `Ctrl+F9`.
4. Installerul va fi creat în `artifacts\installer`.

## Versiune nouă

Pentru exemplul `1.0.1`:

```powershell
.\scripts\make-installer.ps1 -Version '1.0.1' -NumericVersion '1.0.1.0'
```

`Version` este textul public afișat utilizatorilor. `NumericVersion` trebuie să conțină exact patru numere și este folosit de Windows în proprietățile fișierului.

## Ce va vedea utilizatorul

- selectarea limbii română/engleză;
- instalare în `Program Files\StarSim Core`;
- scurtătură în meniul Start;
- scurtătură pe desktop opțională;
- instalarea automată a Visual C++ Runtime numai dacă lipsește;
- opțiunea de a porni StarSim Core la final;
- dezinstalare din Windows Settings → Apps.

Setările, presetările personale și pluginurile instalate de utilizator rămân în `%AppData%\StarSimCore`; dezinstalarea aplicației nu le șterge automat.

## Test minim înainte de publicare

Pe un calculator sau o mașină virtuală curată:

1. porniți installerul;
2. bifați scurtătura de desktop;
3. porniți aplicația;
4. deschideți un TIFF și verificați Wavelets;
5. verificați pluginul demonstrativ în Expert → Plugins;
6. exportați o imagine;
7. dezinstalați aplicația din Windows Settings → Apps.

Testarea recomandată este Windows 11 x64. Windows 10 x64 22H2 trebuie prezentat ca suport „best effort” numai după verificarea pe un sistem curat.
