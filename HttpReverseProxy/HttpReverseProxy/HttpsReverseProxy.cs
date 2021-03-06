﻿namespace HttpReverseProxy
{
  using HttpReverseProxyLib;
  using HttpReverseProxyLib.DataTypes.Class;
  using HttpReverseProxyLib.DataTypes.Enum;
  using HttpReverseProxyLib.DataTypes.Interface;
  using System;
  using System.Collections.Generic;
  using System.IO;
  using System.Net;
  using System.Net.Security;
  using System.Net.Sockets;
  using System.Reflection;
  using System.Security.Authentication;
  using System.Security.Cryptography.X509Certificates;
  using System.Text;
  using System.Threading;


  public sealed class HttpsReverseProxy : HttpReverseProxyBasis, IPluginHost
  {

    #region MEMBERS
    
    private static X509Certificate2 serverCertificate2;
    private static RemoteCertificateValidationCallback remoteCertificateValidation = new RemoteCertificateValidationCallback(delegate { return true; });
    private TcpListener tcpListener;
    private Thread tcpListenerThread;
//    private Lib.PluginCalls pluginCalls;

    #endregion


    #region PROPERTIES

    public static HttpsReverseProxy Server { get; set; } = new HttpsReverseProxy();

    public IPAddress ListeningIpInterface { get; private set; } = IPAddress.Any;

    public int ListeningPort { get; private set; } = Config.LocalHttpsServerPort;

    public List<IPlugin> LoadedPlugins { get; private set; } = Config.LoadedPlugins;

    #endregion


    #region PUBLIC

    public override bool Start(int localServerPort, string certificateFilePath)
    {
      // Initialize general values
      Config.RemoteHostIp = "0.0.0.0";
      //this.pluginCalls = new Lib.PluginCalls();

      // Load all plugins
      //this.LoadAllPlugins();

      // Start listener
      serverCertificate2 = new X509Certificate2(certificateFilePath, string.Empty);
      this.tcpListener = new TcpListener(this.ListeningIpInterface, localServerPort);

      Logging.Instance.LogMessage("TcpListener", ProxyProtocol.Undefined, Loglevel.Info, "HTTPS reverse proxy server started on port {0}", localServerPort, Path.GetFileName(certificateFilePath));

      try
      {
        this.tcpListener.Start();
      }
      catch (Exception ex)
      {
        Logging.Instance.LogMessage("TcpListener", ProxyProtocol.Undefined, Loglevel.Error, "ProxyServer.Start(EXCEPTION): {0}", ex.Message);
        return false;
      }

      this.tcpListenerThread = new Thread(new ParameterizedThreadStart(HandleHttpsClient));
      this.tcpListenerThread.Start(this.tcpListener);

      return true;
    }


    public override void Stop()
    {
      this.tcpListener.Stop();

      // Wait for cRemoteSocket to finish processing current connections...
      if (this.tcpListenerThread?.IsAlive == true)
      {
        this.tcpListenerThread.Abort();
        this.tcpListenerThread.Join();
      }

      // Unload loaded plugins
      //this.UnloadAllPlugins();
    }


    public void LoadPlugin(string pluginFileFullPath)
    {
      Assembly pluginAssembly;

      if ((pluginAssembly = Assembly.LoadFile(pluginFileFullPath)) == null)
      {
        throw new Exception("The plugin file could not be loaded");
      }

      try
      {
        var fileName = Path.GetFileName(pluginFileFullPath);
        fileName = Path.GetFileNameWithoutExtension(fileName);

        var pluginName = $"HttpReverseProxy.Plugin.{fileName}.{fileName}";
        Type objType = pluginAssembly.GetType(pluginName, false, false);
        object tmpPluginObj = Activator.CreateInstance(objType, true);

        if (!(tmpPluginObj is IPlugin))
        {
          throw new Exception("The plugin file does not support the required plugin interface");
        }

        var tmpPlugin = (IPlugin)tmpPluginObj;
        if (Config.LoadedPlugins.Find(elem => elem.Config.Name == tmpPlugin.Config.Name) != null)
        {
          throw new Exception("This plugin was loaded already");
        }

        tmpPlugin.OnLoad(this);
      }
      catch (Exception ex)
      {
        var filename = Path.GetFileName(pluginFileFullPath);
        Console.WriteLine($"An error occurred while loading HTTPS plugin file \"{filename}\": {ex.Message}\r\n{ex.StackTrace}");
      }
    }

    #endregion


    #region PRIVATE

    private static void HandleHttpsClient(object tcpListenerObj)
    {
      TcpListener tcpListener = (TcpListener)tcpListenerObj;
      try
      {
        while (true)
        {
          Logging.Instance.LogMessage("TcpListener", ProxyProtocol.Undefined, Loglevel.Debug, "Waiting for incoming HTTPS request");
          TcpClient tcpClient = tcpListener.AcceptTcpClient();
          tcpClient.NoDelay = true;

          while (!ThreadPool.QueueUserWorkItem(new WaitCallback(HttpsReverseProxy.InitiateHttpsClientRequestProcessing), tcpClient))
          {
            ;
          }
        }
      }
      catch (ThreadAbortException taex)
      {
        Console.WriteLine($"HandleHttpsClient(ThreadAbortException): {taex.Message}");
      }
      catch (SocketException sex)
      {
        Console.WriteLine($"HandleHttpsClient(SocketException): {sex.Message}");
      }
      catch (Exception ex)
      {
        Console.WriteLine($"HandleHttpsClient(Exception): {ex.Message}");
      }
    }


    private static void InitiateHttpsClientRequestProcessing(object clientTcpObj)
    {
      TcpClient tcpClient = (TcpClient)clientTcpObj;
      string clientIp = string.Empty;
      string clientPort = string.Empty;
      string clientMac = string.Empty;
      RequestObj requestObj = new RequestObj(Config.DefaultRemoteHost, ProxyProtocol.Https);

      // Determine tcpClient IP and MAC address.
      try
      {
        Logging.Instance.LogMessage("TcpListener", ProxyProtocol.Https, Loglevel.Debug, "InitiateHttpsClientRequestProcessing(): New HTTPS request initiated");
        string[] splitter = tcpClient.Client.RemoteEndPoint.ToString().Split(new char[] { ':' });
        clientIp = splitter[0];
        clientPort = splitter[1];
      }
      catch (Exception ex)
      {
        Console.WriteLine($"InitiateHttpsClientRequestProcessing(Exception): {ex.Message}");
      }

      try
      {
        clientMac = Lib.Common.GetMacFromNetworkComputer(clientIp);
      }
      catch (Exception)
      {
        clientMac = "00:00:00:00:00:00";
      }

      requestObj.SrcMac = clientMac;
      requestObj.SrcIp = clientIp;
      requestObj.SrcPort = clientPort;
      requestObj.TcpClientConnection = tcpClient;

      // Open tcpClient system's data clientStream
      try
      {
        SslStream sslStream = new SslStream(requestObj.TcpClientConnection.GetStream(), false, new RemoteCertificateValidationCallback(remoteCertificateValidation));
        sslStream.AuthenticateAsServer(serverCertificate2, false, SslProtocols.Tls | SslProtocols.Ssl3, false);

        requestObj.ClientRequestObj.ClientBinaryReader = new MyBinaryReader(requestObj.ProxyProtocol, sslStream, 8192, Encoding.UTF8, requestObj.Id);
        requestObj.ClientRequestObj.ClientBinaryWriter = new BinaryWriter(sslStream);

        RequestHandlerHttp requestHandler = new RequestHandlerHttp(requestObj);
        requestHandler.ProcessClientRequest();
      }
      catch (Exception ex)
      {
        Logging.Instance.LogMessage(requestObj.Id, requestObj.ProxyProtocol, Loglevel.Debug, $"ProxyServer.InitiateHttpsClientRequestProcessing(EXCEPTION): {ex.Message}\r\n{ex.GetType().ToString()}");
        if (ex.InnerException is Exception)
        {
          Logging.Instance.LogMessage(requestObj.Id, requestObj.ProxyProtocol, Loglevel.Debug, $"ProxyServer.InitiateHttpsClientRequestProcessing(INNEREXCEPTION): {ex.InnerException.Message}, {ex.GetType().ToString()}");
        }
      }
      finally
      {
        if (requestObj.ClientRequestObj.ClientBinaryReader != null)
        {
          requestObj.ClientRequestObj.ClientBinaryReader.Close();
          Logging.Instance.LogMessage(requestObj.Id, requestObj.ProxyProtocol, Loglevel.Debug, "ProxyServer.InitiateHttpsClientRequestProcessing(): ClientBinaryReader.Close()");
        }

        if (requestObj.ClientRequestObj.ClientBinaryWriter != null)
        {
          requestObj.ClientRequestObj.ClientBinaryWriter.Close();
          Logging.Instance.LogMessage(requestObj.Id, requestObj.ProxyProtocol, Loglevel.Debug, "ProxyServer.InitiateHttpsClientRequestProcessing(): ClientBinaryWriter.Close()");
        }

        if (requestObj.ServerRequestHandler != null)
        {
          requestObj.ServerRequestHandler.CloseServerConnection();
          Logging.Instance.LogMessage(requestObj.Id, requestObj.ProxyProtocol, Loglevel.Debug, "ProxyServer.InitiateHttpsClientRequestProcessing(): ServerRequestHandler.CloseServerConnection())");
        }

        if (requestObj.TcpClientConnection != null)
        {
          requestObj.TcpClientConnection.Close();
          Logging.Instance.LogMessage(requestObj.Id, requestObj.ProxyProtocol, Loglevel.Debug, "ProxyServer.InitiateHttpsClientRequestProcessing(): TcpClientConnection.Close()");
        }
      }
    }


    /// <summary>
    ///
    /// </summary>
    private void LoadAllPlugins()
    {
      string pluginsPath = Path.Combine(Directory.GetCurrentDirectory(), "plugins");
      string[] pluginDirs;

      if (!Directory.Exists(pluginsPath))
      {
        return;
      }

      pluginDirs = Directory.GetDirectories(pluginsPath);

      // Iterate through all plugin directories
      foreach (string tmpPluginDir in pluginDirs)
      {
        string[] pluginFiles = Directory.GetFiles(tmpPluginDir, "*.dll");

        // Load all plugin files, instantiate an object and initialize plugin.
        foreach (string pluginFileFullPath in pluginFiles)
        {
          try
          {
            this.LoadPlugin(pluginFileFullPath);
          }
          catch (Exception ex)
          {
            Console.WriteLine("An error occurred while loading the plugin \"{0}\": {1}", Path.GetFileNameWithoutExtension(pluginFileFullPath), ex.Message);
          }
        }
      }
    }


    private void UnloadAllPlugins()
    {
      List<IPlugin> tmpPluginList = new List<IPlugin>();
      tmpPluginList.AddRange(Config.LoadedPlugins);

      foreach (IPlugin tmpPlugin in tmpPluginList)
      {
        tmpPlugin.OnUnload();
        Config.LoadedPlugins.Remove(tmpPlugin);
      }
    }

    #endregion


    #region SSL debugging methods (copied from MSDN)

    static void DisplaySecurityLevel(SslStream stream)
    {
      Console.WriteLine("Cipher: {0} strength {1}", stream.CipherAlgorithm, stream.CipherStrength);
      Console.WriteLine("Hash: {0} strength {1}", stream.HashAlgorithm, stream.HashStrength);
      Console.WriteLine("Key exchange: {0} strength {1}", stream.KeyExchangeAlgorithm, stream.KeyExchangeStrength);
      Console.WriteLine("Protocol: {0}", stream.SslProtocol);
    }


    static void DisplaySecurityServices(SslStream stream)
    {
      Console.WriteLine("Is authenticated: {0} as server? {1}", stream.IsAuthenticated, stream.IsServer);
      Console.WriteLine("IsSigned: {0}", stream.IsSigned);
      Console.WriteLine("Is Encrypted: {0}", stream.IsEncrypted);
    }


    static void DisplayStreamProperties(SslStream stream)
    {
      Console.WriteLine("Can read: {0}, write {1}", stream.CanRead, stream.CanWrite);
      Console.WriteLine("Can timeout: {0}", stream.CanTimeout);
    }


    static void DisplayCertificateInformation(SslStream stream)
    {
      Console.WriteLine("Certificate revocation list checked: {0}", stream.CheckCertRevocationStatus);

      X509Certificate localCertificate = stream.LocalCertificate;
      if (stream.LocalCertificate != null)
      {
        Console.WriteLine("Local cert was issued to {0} and is valid from {1} until {2}.",
            localCertificate.Subject,
            localCertificate.GetEffectiveDateString(),
            localCertificate.GetExpirationDateString());
      }
      else
      {
        Console.WriteLine("Local certificate is null.");
      }

      // Display the properties of the client's certificate.
      X509Certificate remoteCertificate = stream.RemoteCertificate;
      if (stream.RemoteCertificate != null)
      {
        Console.WriteLine("Remote cert was issued to {0} and is valid from {1} until {2}.",
            remoteCertificate.Subject,
            remoteCertificate.GetEffectiveDateString(),
            remoteCertificate.GetExpirationDateString());
      }
      else
      {
        Console.WriteLine("Remote certificate is null.");
      }
    }

    #endregion


    #region INTERFACE: IPluginHost

    /// <summary>
    ///
    /// </summary>
    /// <param name="pluginData"></param>
    public void RegisterPlugin(IPlugin pluginData)
    {
      if (pluginData == null)
      {
        return;
      }

      lock (Config.LoadedPlugins)
      {
        List<IPlugin> foundPlugins = Config.LoadedPlugins.FindAll(elem => elem.Config.Name == pluginData.Config.Name);
        if (foundPlugins == null || foundPlugins.Count <= 0)
        {
          Config.AddNewPlugin(pluginData);
          Logging.Instance.LogMessage("HttpsReverseProxy", ProxyProtocol.Undefined, Loglevel.Info, "Registered plugin \"{0}\"", pluginData.Config.Name);
        }
      }
    }


    public Logging LoggingInst { get { return Logging.Instance; } set { } }

    #endregion

  }
}
