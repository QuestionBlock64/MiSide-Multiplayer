# MiSide Multiplayer Mod

## Overview
The MiSide Multiplayer Mod is a pioneering modification that introduces full multiplayer support to MiSide for the first time. Currently, the modification has been tested for a three-player cooperative experience, with potential support for more players.

## Current Limitations & Roadmap
This modification is currently in active development. While the core connection framework and **all player animations (including custom models via the MiSide Custom Model Loader)** are now fully functional and optimized, please be aware that the following features are still undergoing development:
* **Mita Synchronization**: Initial implementation is live. Basic positional and behavioral states sync, but advanced interactions are still undergoing stabilization.
* **Mission Synchronization**: Preliminary objective and quest tracking are active, but complex progression triggers are still prone to desynchronization.
* **Environment Synchronization**: World state changes, triggers, and environmental updates are available, but they might be prone to bugs.
* **Object and Inventory Synchronization**: Proper handling of item pickups and inventory states is available, but it might be prone to bugs.

## Installation & Setup

### Prerequisites
* A clean installation of MiSide.
* BepInEx framework installed in your game directory.

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
