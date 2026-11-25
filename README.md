# Temporal Storm HUD

Mod do **Vintage Story 1.22.x** (testowany na 1.22.7), działający tylko po stronie klienta – także na cudzych serwerach.

Wyświetla w lewym górnym rogu ekranu stały HUD:

```
Burza temporalna (lekka) za 18,0 dnia

Aktywność wyrw: spokojne, przez 9 h 40 min
```

- **Burza temporalna** – siła i czas do następnej burzy (od doby w górę w dniach z jednym miejscem po przecinku, poniżej w godzinach i minutach gry); w trakcie burzy – czas do jej końca.
- **Aktywność wyrw** – aktualny poziom (nazwa z tłumaczenia gry) i ile godzin gry jeszcze potrwa.

Czas jest liczony w czasie gry. Teksty HUD-u są po polsku.

## Instalacja

Pobierz `tempstormhud_X.Y.Z.zip` z zakładki Releases i wrzuć **bez rozpakowywania** do folderu `VintagestoryData/Mods`. Usuń wcześniejszą wersję moda, jeśli była.

## Sterowanie

`Ctrl+Shift+T` – pokaż/ukryj HUD (zmiana w ustawieniach sterowania gry).

## Ograniczenia

- Po `/nexttempstorm now` serwer nie wysyła klientom aktualizacji, więc licznik burzy pokazuje starą wartość do chwili, gdy burza się zacznie.
- Linia aktywności wyrw pojawia się dopiero, gdy serwer prześle dane (przy wejściu do świata).

## Budowanie

Wymagania: .NET 10 SDK i zmienna środowiskowa `VINTAGE_STORY` wskazująca folder instalacji gry:

```powershell
[Environment]::SetEnvironmentVariable("VINTAGE_STORY", "$Env:AppData\Vintagestory", "User")
```

```powershell
dotnet build                  # kompilacja kontrolna + zip w bin/release/
dotnet build -p:Deploy=true   # dodatkowo podmienia moda w %AppData%\VintagestoryData\Mods
```

Mod jest dystrybuowany jako zip ze źródłem `.cs` – gra kompiluje go sama przy starcie. Projekt `.csproj` służy do sprawdzenia kodu z bibliotekami gry i do pakowania.

## Licencja

MIT – zobacz [LICENSE](LICENSE).
