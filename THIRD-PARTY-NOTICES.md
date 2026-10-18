# Third-party notices

OmenFanPlus is distributed under the MIT license (see LICENSE). It uses these components, unmodified,
through NuGet:

| Component | License | Source |
| --- | --- | --- |
| LibreHardwareMonitorLib 0.9.6 | MPL-2.0 | https://github.com/LibreHardwareMonitor/LibreHardwareMonitor |
| PawnIO modules embedded in LibreHardwareMonitorLib (IntelMSR, LpcACPIEC, ...) | LGPL-2.1 | https://github.com/namazso/PawnIO.Modules |
| BlackSharp.Core, DiskInfoToolkit, RAMSPDToolkit-NDD (LibreHardwareMonitorLib dependencies) | MPL-2.0 | see each package on nuget.org |
| HidSharp (LibreHardwareMonitorLib dependency) | Apache-2.0 | https://www.zer7.com/software/hidsharp |
| Mono.Posix.NETStandard | MIT | https://github.com/mono/mono |
| TaskScheduler (Microsoft.Win32.TaskScheduler) | MIT | https://github.com/dahall/TaskScheduler |
| WindowsInput | MIT | https://github.com/MediatedCommunications/WindowsInput |
| Newtonsoft.Json | MIT | https://github.com/JamesNK/Newtonsoft.Json |
| System.Management, System.IO.Ports | MIT | https://github.com/dotnet/runtime |

The MPL-2.0 and LGPL-2.1 components are used as unmodified libraries; their source is available at the links
above. PawnIO itself (the driver) is not bundled; it is installed separately from https://pawnio.eu.

HPShimLibrary contains code and data (device lists) derived from HP's OMEN Gaming Hub; the MIT license of this
project does not cover HP's material.
