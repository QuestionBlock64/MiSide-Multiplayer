# MiSide Multiplayer Mod

## Overview
The MiSide Multiplayer Mod is a pioneering modification that introduces full multiplayer support to MiSide for the first time. Currently, the modification has been tested for a two-player cooperative experience, with potential support for additional players pending further testing and development. 

## Current Limitations & Roadmap
This modification is currently in active development. While the core connection framework is established, the following features are actively being worked on to ensure a seamless experience:
* **Environment Synchronization**: Ensuring world state changes are reflected across all clients.
* **Mission Synchronization**: Shared objective tracking and completion.
* **Object and Inventory Synchronization**: Proper handling of item pickups and inventory states.
* **Player Animations**: Implementing animation controllers for puppet players to resolve sliding during movement.
* **Mitas Synchronization**: Fully syncing Mitas states and interactions across connected clients.

## Installation & Setup

### Prerequisites
* A clean installation of MiSide.
* BepInEx framework installed in your game directory.

### Host Instructions
1. Locate and execute `MiSideMultiplayerRelayServer.exe` to start the host server.
2. Ensure your local configuration file is set up with your preferred display name and customizations (see the Configuration section below).
3. Provide your IP address to the connecting client. 
*Security Notice: It is strongly advised not to share your raw public IP address. Instead, utilize a secure port forwarding service or a Virtual LAN software such as Hamachi, Radmin VPN, or ZeroTier.*

### Client Instructions
1. Ensure the Host has successfully started the relay server and provided you with their secure IP address.
2. Navigate to your MiSide installation directory, specifically: `[MiSideInstallDirectory]\BepInEx\Config`.
3. Open the configuration file named `com.miside.multiplayer.puppets.cfg` using a standard text editor.
4. Locate the `Networking` section within the file.
5. Enter the Host's IP address in the designated IP field.
6. Save the file and launch the game.

## Configuration
Player names, visual customizations, and network settings are managed entirely within the `com.miside.multiplayer.puppets.cfg` file. 
* Both the Host and the Client must configure their respective names and customizations within this file.
* Detailed explanations for every customizable parameter are documented directly inside the configuration file itself.

## Testing and Feedback
As this is the first multiplayer implementation for MiSide, community testing is invaluable. Further testing is required to determine the absolute maximum player count stability. Feedback on synchronization limits and bug reports are highly appreciated.

## Bug Reporting & Issues

As this is the first multiplayer implementation for MiSide, community testing is invaluable. Because this is an experimental release, it is highly prone to uncatalogued bugs and synchronization limits that are not listed above.

If you encounter any undocumented bugs, crashes, or issues, please open a new Issue on this GitHub repository. Provide as much detail as possible, including any relevant console logs and the exact steps to reproduce the error.

## Contact & Support

For direct feedback, inquiries regarding maximum player count stability, or further assistance regarding the modification, you can reach out directly:

* Discord: QB64 (you must send a friend request, however i may not be accepting so this option may or may not work)
* Alternatively, you may join  my very own discord server: https://discord.gg/XCZ9rka9em

---
*Developed by QuestionBlock64*
