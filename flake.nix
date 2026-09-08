{
  description = "wolverine-deploy-sim — churn simulator and the SafetyLab invariant monitor";

  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixos-26.05";

  outputs = { self, nixpkgs }:
    let
      systems = [ "x86_64-linux" "aarch64-linux" "x86_64-darwin" "aarch64-darwin" ];
      forAllSystems = f: nixpkgs.lib.genAttrs systems (system: f {
        inherit system;
        pkgs = import nixpkgs { inherit system; };
      });
    in
    {
      devShells = forAllSystems ({ pkgs, system }: {
        # ChurnSim itself still builds inside the SDK container (that is the point --
        # the image is what runs in minikube). This shell is for SafetyLab: the monitor
        # is containerised too, but `safetylab check` runs on the host over a captured
        # run directory, and wanting a local SDK for that is the whole reason it exists.
        default = pkgs.mkShell {
          packages = [ pkgs.dotnetCorePackages.sdk_10_0 pkgs.podman pkgs.kubectl pkgs.jq ];

          env = {
            DOTNET_CLI_TELEMETRY_OPTOUT = "1";
            DOTNET_NOLOGO = "1";
            DOTNET_ROOT = "${pkgs.dotnetCorePackages.sdk_10_0}/share/dotnet";
          };

          shellHook = ''
            echo "wolverine-deploy-sim shell — .NET $(dotnet --version)"
          '';
        };
      });
    };
}
