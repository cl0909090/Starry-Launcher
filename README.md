# Starry Launcher

A third-party launcher for Minecraft: Java Edition (Windows desktop application).

## Description

Browse and install Minecraft: Java Edition releases/snapshots, mods, modpacks, resource packs, shaders, data packs and maps; manage multiple local game instances; launch the game.

## Authentication

- Microsoft sign-in only, using the standard OAuth 2.0 authorization code flow with PKCE.
- All downloads and game launching are gated behind a successful Microsoft sign-in — users cannot download any content without being signed in with a genuine Microsoft account.
- Access/refresh tokens are stored locally on the player's machine only, and can only be refreshed through the official Microsoft token endpoint.

## Features

- Official version list retrieval; automatic download of version JSON, client jar, libraries and assets
- Version selection and game launching
- Automatic memory allocation, natives extraction
- Local sign-in state caching
