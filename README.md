# Jenkins Agents as Docker Images

This repository contains Dockerfiles to build several different Jenkins agents. All of them can be used the same as jenkins/inbound-agent.

## basic-agent
This agent contains:
- A Java installation.
- A Git client.
- A Plastic client.

## ci-agent
This agent contains everything above as well as:
- A Node.js client (npm).
- An Itch.io client (butler).
- A Steam client (steamcmd).
- An installation of [slothsoft/unity](https://github.com/Faulo/slothsoft-unity) (compose-unity).

## unity-agent
This agent contains everything above as well as:
- A .NET installation.
- A Unity Hub installation.
- A VNC server to set up Unity licensing.