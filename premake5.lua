workspace "nut-vermaak-partituren"
architecture "x64"
   configurations { "Debug", "Release" }
   startproject "nut-vermaak-partituren"

   project "nut-vermaak-partituren"
      kind "ConsoleApp" -- CLI application
      dotnetframework "net10.0"
      location "nut-vermaak-partituren"
      dotnetsdk "Web"
      language "C#"
      targetdir "bin/%{cfg.buildcfg}"
      files { "%{prj.name}/src/**.cs", "%{prj.name}/src/**.env" }       -- Include all C# source files
      nuget { "Blazor.Bootstrap:4.0.0", "DotNetEnv:3.2.0",  "Microsoft.AspNetCore.OpenApi:10.0.0", "Swashbuckle.AspNetCore:10.0.1", "Microsoft.AspNetCore.Mvc.Razor.RuntimeCompilation:10.0.15", "ClosedXML:0.105.0", "Google.Apis.Drive.v3:1.78.0.4327","Microsoft.EntityFrameworkCore.InMemory:10.0.15","Microsoft.AspNetCore.Authentication.Google:10.0.15", "Microsoft.AspNetCore.Authentication.MicrosoftAccount:10.0.15" }
      vsprops {
         PublishSingleFile = "true",
         SelfContained = "true",
         IncludeNativeLibrariesForSelfExtract = "true"
      }

      filter "configurations:Debug"
         defines { "DEBUG" }
         optimize "Off"
      
      filter "configurations:Release"
         symbols "Off"
         defines { "NDEBUG" }
         optimize "On"