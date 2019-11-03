using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Configuration.Install;
using System.ServiceProcess;
using System.Linq;
using System.Threading.Tasks;

namespace XRPLCrawler
{
    [RunInstaller(true)]
    public partial class XRPLCrawlerServiceInstaller : Installer
    {
        private ServiceInstaller serviceInstaller;
        private ServiceProcessInstaller processInstaller;

        public XRPLCrawlerServiceInstaller()
        {
            // Instantiate installer for process and service.
            processInstaller = new ServiceProcessInstaller();
            serviceInstaller = new ServiceInstaller();

            // The service runs under the system account.
            processInstaller.Account = ServiceAccount.LocalSystem;

            // The service is started manually.
            serviceInstaller.StartType = ServiceStartMode.Automatic;

            // ServiceName must equal those on ServiceBase derived classes.
            serviceInstaller.ServiceName = Program.ServiceName;

            // Add installer to collection. Order is not important if more than one service.
            Installers.Add(serviceInstaller);
            Installers.Add(processInstaller);

            this.AfterInstall += new InstallEventHandler(ServiceInstaller_AfterInstall);
        }

        void ServiceInstaller_AfterInstall(object sender, InstallEventArgs e)
        {
            using (ServiceController sc = new ServiceController(serviceInstaller.ServiceName))
            {
                sc.Start();
            }
        }
    }
}
