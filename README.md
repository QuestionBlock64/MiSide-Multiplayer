# MiSide Multiplayer Mod

## Overview
The MiSide Multiplayer Mod is a pioneering modification that introduces full multiplayer support to MiSide for the first time. The framework has been significantly expanded with a new connectivity system, featuring matchmaking lobbies (Public, Private, and Direct Connect), dedicated single-player and multiplayer separation, expanded support for up to 4 players per match, and full synchronization across Mita, world states, quests, and interactive objects.

## Current Limitations & Roadmap
This modification is currently in active development. While the core connection framework and **all player animations (including custom models via the MiSide Custom Model Loader)** are now fully functional and optimized, please be aware that the following features are still undergoing development:

* **Mita Latency:** Mita's position and animation are broadcast from the host client. On higher-ping connections, her remote copy may appear slightly behind the host's copy due to standard network latency.

* **Environmental & Physics Sync:** Certain full-world environmental state changes and physics-driven props may still experience minor state drift.

### First-Time Setup
After installing the mod via BepInEx, you must launch the game once and then close it. This allows the mod to generate the configuration file required for the hosting and client setup steps in the server's repository. (https://github.com/QuestionBlock64/MiSideMultiplayerServer)

## Configuration
Player names, visual customizations, and network settings are managed entirely within the `MS_Multiplayer.cfg` file. 
* Both the Host and the Client must configure their respective names and customizations within this file.
* Detailed explanations for every customizable parameter are documented directly inside the configuration file itself.

## Testing and Feedback
As this is the first multiplayer implementation for MiSide, community testing is invaluable. Further testing is required to determine the absolute maximum player count stability. Feedback on synchronization limits and bug reports are highly appreciated.

## Bug Reporting & Issues
Because this is an experimental release, it is highly prone to uncatalogued bugs and synchronization limits that are not listed above.

If you encounter any undocumented bugs, crashes, or issues, please open a new Issue on this GitHub repository. Provide as much detail as possible, including any relevant console logs and the exact steps to reproduce the error.

## Contact & Support
For direct feedback, inquiries regarding maximum player count stability, or further assistance regarding the modification, you can reach out directly:

* Discord: QB64 (you must send a friend request, however i may not be accepting so this option may or may not work)
* Alternatively, you may join my very own discord server: https://discord.gg/yyDVE3rTzQ

---
*Developed by QuestionBlock64*
