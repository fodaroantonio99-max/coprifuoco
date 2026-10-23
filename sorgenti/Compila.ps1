# Compila Coprifuoco.exe e "Installa Coprifuoco.exe" con il compilatore C# che c'è già in Windows (.NET Framework 4).
# Non serve installare nulla. Uso: tasto destro -> Esegui con PowerShell (oppure: powershell -File Compila.ps1)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$out  = Split-Path $here -Parent
$fw   = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc  = Join-Path $fw 'csc.exe'

# 1) l'icona, se manca
if (-not (Test-Path (Join-Path $here 'coprifuoco.ico'))) { & (Join-Path $here 'Disegna-icona.ps1') }

# 2) il programma
$wpf = 'PresentationFramework.dll,PresentationCore.dll,WindowsBase.dll,System.Xaml.dll'
$std = 'System.Core.dll,System.Drawing.dll,System.Windows.Forms.dll,Microsoft.CSharp.dll,System.Runtime.WindowsRuntime.dll'
Push-Location $here
& $csc /nologo /target:winexe /optimize+ /codepage:65001 "/lib:$fw\WPF" "/r:$wpf,$std" `
    "/win32icon:coprifuoco.ico" "/out:$out\Coprifuoco.exe" `
    "/resource:Main.xaml,Main.xaml" "/resource:Popup.xaml,Popup.xaml" "/resource:Riavvio.xaml,Riavvio.xaml" "/resource:coprifuoco.ico,Coprifuoco.ico" Coprifuoco.cs
if ($LASTEXITCODE -ne 0) { Pop-Location; throw 'Compilazione di Coprifuoco.exe fallita' }

# 3) l'installer, che porta dentro Coprifuoco.exe
& $csc /nologo /target:winexe /optimize+ /codepage:65001 "/r:System.Core.dll,System.Drawing.dll,System.Windows.Forms.dll,Microsoft.CSharp.dll" `
    "/win32icon:coprifuoco.ico" "/out:$out\Installa Coprifuoco.exe" `
    "/resource:$out\Coprifuoco.exe,Coprifuoco.exe" "/resource:coprifuoco.ico,Coprifuoco.ico" Installer.cs
if ($LASTEXITCODE -ne 0) { Pop-Location; throw 'Compilazione di Installa Coprifuoco.exe fallita' }
Pop-Location
Write-Host 'Fatto: Coprifuoco.exe e Installa Coprifuoco.exe sono nella cartella sopra.'
