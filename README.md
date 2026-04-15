# Unity Docker Image

This repository contains Dockerfiles to build an image that can run Unity.

## compose-unity
This agent contains:
- A Node.js client (npm).
- An Itch.io client (butler).
- A Steam client (steamcmd).
- An installation of [slothsoft/unity](https://github.com/Faulo/slothsoft-unity) (compose-unity).
- A .NET installation.
- A Unity Hub installation.
- A VNC server to set up Unity licensing.