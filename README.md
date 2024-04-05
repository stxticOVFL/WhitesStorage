# White's Storage
## A dedicated mod updater/manager for Neon White. 

![image](https://github.com/stxticOVFL/WhitesStorage/assets/29069561/4274e4c4-e49f-4f35-9ff1-3c57318e0733)

## Features
- Community-driven, always up-to-date 0-click downloads and updates (feel free to PR to add your/others mods! Just modify `Updates.txt`)
- Easy enable/disable of any mod, not just auto-updated ones
- Easy dependency handling for things such as UniverseLib for mods that need it

## Current Mods
- **[MelonPreferencesManager](https://github.com/Bluscream/MelonPreferencesManager)** *(1.3.1)* by Bluescream/kafeijao
- **[NeonLite](https://github.com/MOPSKATER/NeonLite)** *2.2.1* by MOPSKATER/faustas
- **[GUnJammer](https://github.com/MOPSKATER/GUnJammer)** *0.1.0* by MOPSKATER
- **[Puppy Powertools](https://github.com/PandorasFox/NeonWhite-PuppyPowerTools)** *1.5.6* by PandorasFox
- **[Event Tracker](https://github.com/stxticOVFL/EventTracker)** *2.0.1* by stxticOVFL 
- **[Input Display](https://github.com/stxticOVFL/NeonInputDisplay)** *1.0.2-a* by stxticOVFL
- **[NeonCapture](https://github.com/stxticOVFL/NeonCapture)** *1.0.0* by stxticOVFL
- **[YourStory](https://github.com/stxticOVFL/YourStory)** *1.0.0* by stxticOVFL

## Installation
1. Download [MelonLoader](https://github.com/LavaGang/MelonLoader/releases/latest) and install it onto your `Neon White.exe`.
2. Run the game once. This will create required folders.
3. Download `WhitesStorage.dll` from the [Releases page](https://github.com/stxticOVFL/WhitesStorage/releases/latest) and drop it in the **`Plugins`** (__***NOT***__ `Mods`) folder.
4. Enable the mods you want using the preferences manager (using F5) and just restart whenever you make changes!

## Building & Contributing
This project uses Visual Studio 2022 as its project manager. When opening the Visual Studio solution, ensure your references are corrected by right clicking and selecting `Add Reference...` as shown below. 
Both `MelonLoader` and `Mono.Cecil` will be in `Neon White/MelonLoader/net35`, **not** `net6`.
If you get any weird errors, try deleting the references and re-adding them manually.

![image](https://github.com/stxticOVFL/WhitesStorage/assets/29069561/ac4efb7d-e9e2-4287-be33-15fa3fd6fcab)

Once your references are correct, build using the keybind or like the picture below.

![image](https://github.com/stxticOVFL/EventTracker/assets/29069561/40a50e46-5fc2-4acc-a3c9-4d4edb8c7d83)

Make any edits as needed, and make a PR for review. PRs are very appreciated.

(shoutout to faustas for the name)
