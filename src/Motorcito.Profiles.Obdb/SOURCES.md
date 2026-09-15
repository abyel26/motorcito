# OBDb signal definitions

The files in `Signalsets/` are vehicle signal definitions from the **OBDb** community project
(<https://obdb.community>, <https://github.com/OBDb>), redistributed **unmodified**.

They are licensed under the **Creative Commons Attribution-ShareAlike 4.0 International License**
(CC BY-SA 4.0, <https://creativecommons.org/licenses/by-sa/4.0/>). Copyright remains with the OBDb
contributors.

- **Attribution** is shown in the app wherever a value defined by these files is displayed, via
  `ObdbProfileSource.Attribution`.
- **Share-alike** applies to these data files. If a file here is ever modified and distributed, the
  modified file must stay CC BY-SA 4.0. Motorcito's own code is not affected, and definitions Motorcito
  confirms on real cars are kept in its own format, outside this project.
- Before a commercial App Store release, have the licence position reviewed (in particular whether App
  Store distribution counts as an additional restriction). The exact files are published in this
  repository, which is intended to keep them freely available.

## Vendored files

| File | Source repository | Commit |
|---|---|---|
| `Signalsets/Mazda-MX-5.json` | <https://github.com/OBDb/Mazda-MX-5> (`signalsets/v3/default.json`) | `3ea0aa156c8cab1b095b83d8ba0100614798d52a` |

## Removing OBDb from the app

Nothing outside this project depends on it. To remove it:

1. Delete `src/Motorcito.Profiles.Obdb/` and `tests/Motorcito.Profiles.Obdb.Tests/`.
2. Remove both projects from `Motorcito.sln`.
3. Remove the `ProjectReference` to this project from `src/Motorcito.App/Motorcito.App.csproj`.
4. Remove the `ObdbProfileSource` registration in `src/Motorcito.App/MauiProgram.cs`.
5. Optional: delete stored readings that came from OBDb definitions.

The app then runs with standard OBD data only, and the attribution line disappears on its own.
`ArchitectureTests` in the core test projects fail if the core ever starts depending on this project.
