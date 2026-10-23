# Coprifuoco

Un piccolo programma per Windows che spegne il PC all'ora che dici tu. Spegne, riavvia, iberna, sospende, disconnette o blocca, e funziona tutto in locale, senza internet.

Resta nascosto vicino all'orologio e a finestra chiusa occupa pochi MB di RAM.

## Cosa fa

- **Tra un po'**: un timer in ore e minuti.
- **A un'ora precisa**: per esempio alle 23:30 (se l'ora è già passata vale per domani). Con **Ripeti** diventa una regola fissa: ogni giorno, oppure solo i giorni che scegli, alle 00:01 o all'ora che vuoi, senza riaprire Coprifuoco ogni volta.
- **Se non lo uso**: agisce dopo un certo tempo senza mouse e tastiera, ma si ferma finché c'è un video o dell'audio in riproduzione, così non ti spegne il film mentre lo guardi dal letto. Si può limitare a una fascia oraria, per esempio solo di notte.
- **Cinque minuti prima** compare un avviso in basso a destra: lasci fare oppure rimandi di 30 minuti, 1 ora o 4 ore. Se non premi niente, procede comunque all'ora prevista.

### I timer sopravvivono al riavvio

Il timer in corso viene salvato su disco e Coprifuoco parte con Windows (lo attiva da solo quando metti un timer). Se il PC viene riavviato prima della scadenza, il timer riparte e compare un banner: «Ti ricordi di quel timer? Lo tengo valido?». Se non premi niente resta valido, se premi **No, annulla** viene tolto.

Se il PC era spento o in sospensione all'ora prevista, al risveglio **non** spegne di colpo: salta quella scadenza (se è passata da più di 5 minuti) e, con la ripetizione, passa alla prossima.

Se Windows resta fermo su una schermata di conferma («questa app impedisce l'arresto») e nessuno risponde, dopo un minuto Coprifuoco forza la chiusura.

Il tema è scuro e caldo, per non accecarti a mezzanotte.

## Installazione

1. Compila il progetto (vedi sotto) oppure scarica i file `.exe` già pronti, se li trovi nelle release.
2. Doppio clic su `Installa Coprifuoco.exe`, scegli cosa vuoi e premi **Installa**.
   Non servono permessi di amministratore e non serve installare altro: basta Windows 10 o 11.
3. Coprifuoco compare vicino all'orologio (a volte dentro la freccia `^`). Doppio clic sull'icona per aprirlo, tasto destro per il menu.

`Coprifuoco.exe` funziona anche da solo, senza installarlo.

Per toglierlo: Impostazioni di Windows → App → Coprifuoco → Disinstalla, oppure di nuovo l'installer.

## Compilare

Non serve Visual Studio: basta il compilatore C# che c'è già in Windows (.NET Framework 4).

```powershell
powershell -ExecutionPolicy Bypass -File sorgenti\Compila.ps1
```

Crea `Coprifuoco.exe` e `Installa Coprifuoco.exe` nella cartella principale. Se manca l'icona la ridisegna da sola con `Disegna-icona.ps1`.

## Com'è fatto

| File | A cosa serve |
| --- | --- |
| `sorgenti/Coprifuoco.cs` | il programma: logica, sensori, area di notifica |
| `sorgenti/Main.xaml`, `Popup.xaml`, `Riavvio.xaml` | la finestra principale, l'avviso dei 5 minuti e il banner dopo il riavvio |
| `sorgenti/Installer.cs` | l'installer (con finestra, senza permessi di amministratore) |
| `sorgenti/Disegna-icona.ps1` | disegna l'icona e genera `coprifuoco.ico` |
| `sorgenti/Compila.ps1` | compila tutto |

Come capisce che stai guardando un video: chiede a Windows se c'è una riproduzione multimediale in corso (YouTube, Netflix, Spotify, VLC...) e controlla il livello dell'audio in uscita.

## Opzioni da riga di comando

- `--background`: parte nascosto, solo con l'icona vicino all'orologio.
- `--dryrun`: prova, non spegne davvero.
- `--selftest`: controlla che la logica, i sensori e le finestre funzionino.
- `--instance=nome`: copia di prova separata dall'app vera (niente icona, niente suoni, finestre fuori schermo). Con la variabile d'ambiente `COPRIFUOCO_DATA` si sceglie dove salvare impostazioni e registro.

## Note

- **Iberna** funziona solo se l'ibernazione è attiva in Windows (`powercfg /hibernate on`).
- Impostazioni e registro stanno in `%APPDATA%\Coprifuoco`.
- I file `.exe` non sono firmati: Windows SmartScreen può avvisare. Scegli "Ulteriori informazioni" e poi "Esegui comunque".
