#!/bin/bash
cd "$(dirname "$0")/.."
NUGET_PACKAGES=/Users/a502/IdeaProjects/mapleStoryMiniPet/.nuget-packages \
  "/Volumes/SSD/C#/dotnet" run --project MiniPet.CrashSelfTest/MiniPet.CrashSelfTest.csproj \
  -p:UsedAvaloniaProducts= -p:StartupObject=MiniPet.CrashSelfTest.SettingsPreviewSelfTest
