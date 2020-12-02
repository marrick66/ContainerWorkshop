using Docker.DotNet;
using Microsoft.Windows.ComputeVirtualization;
using Microsoft.Windows.ComputeVirtualization.Schema;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ShowNetworkAdapters
{
    class Program
    {
        static async Task Main(string[] args)
        {

            //This command is what will be run inside the container, to demonstrate the network and file system
            //isolation:
            var command = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe Get-NetAdapter";

            //This is the base image for the file system that the container uses:
            var imageTag = "mcr.microsoft.com/dotnet/framework/runtime:4.8";
            var client = new DockerClientConfiguration()
                   .CreateClient();

            //Each container image has layers, which represent changes between 
            //image versions. This reads the image configuration to get all these layers
            //needed for starting the container.
            var imageInfo = await client.Images.InspectImageAsync(imageTag);

            var baseImageDir = imageInfo.GraphDriver.Data["dir"];
            var layerConfigText = File.ReadAllText($"{baseImageDir}\\layerchain.json");
            dynamic layerConfig = JsonConvert.DeserializeObject(layerConfigText);

            var layers = new List<Layer>();

            foreach (string layer in layerConfig)
            {
                layers.Add(
                    new Layer
                    {
                        Id = Guid.NewGuid(),
                        Path = layer
                    });
            }

            //This generates a unique ID for the container we're creating:
            var newContainerId = Guid.NewGuid();

            //This is the temporary local isolated sandbox for the container's filesystem:
            var newSandboxPath = $"c:\\testfiles\\sandbox\\{newContainerId}";

            ContainerStorage.CreateSandbox(newSandboxPath, layers);

            //Windows containers use Hyper-V virtual switches, this gets the
            //ID of the one we want to connect the container to:
            var virtualNetId = HostComputeService.FindNetwork(NetworkMode.NAT);
            try
            {
                Console.Out.WriteLine("Creating container...");

                var cs = new ContainerSettings
                {
                    SandboxPath = newSandboxPath,
                    Layers = layers,
                    KillOnClose = true,
                    NetworkId = virtualNetId,
                };
                using (var container = HostComputeService.CreateContainer(newContainerId.ToString(), cs))
                {
                    Console.Out.WriteLine("Starting container...");
                    Console.Out.Flush();
                    container.Start();
                    try
                    {
                        //Once the container is up and running, this starts
                        //the application that should be run in it:
                        var si = new ProcessStartInfo
                        {
                            CommandLine = command,
                            RedirectStandardOutput = true,
                            KillOnClose = true,
                        };
                        using (var process = container.CreateProcess(si))
                        {
                            Console.Out.Write(process.StandardOutput.ReadToEnd());
                            process.WaitForExit(5000);
                            Console.Out.WriteLine("Process exited with {0}.", process.ExitCode);
                        }
                    }
                    finally
                    {
                        Console.Out.WriteLine("Shutting down container...");
                        container.Shutdown(Timeout.Infinite);
                    }
                }
            }
            finally
            {
                //This deletes the temporary file system sandbox:
                ContainerStorage.DestroyLayer(newSandboxPath);
            }

        }
    }
}
 