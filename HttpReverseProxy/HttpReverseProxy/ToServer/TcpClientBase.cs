﻿namespace HttpReverseProxy.ToServer
{
  using HttpReverseProxyLib;
  using HttpReverseProxyLib.DataTypes;
  using HttpReverseProxyLib.Exceptions;
  using HttpReverseProxyLib.Interface;
  using System;
  using System.Collections;
  using System.IO;
  using System.Net.Sockets;
  using System.Text;
  using System.Text.RegularExpressions;


  public class TcpClientBase : TcpClientRaw, IOutgoingRequestClient
  {

    #region MEMBERS

    protected RequestObj requestObj;

    protected MyBinaryReader webServerStreamReader;
    protected BinaryWriter webServerStreamWriter;

    protected MyBinaryReader clientStreamReader;
    protected BinaryWriter clientStreamWriter;

    protected TcpClient httpWebServerSocket;
    protected int remoteTcpPort;

    private const int MaxBufferSize = 4096;

    #endregion


    #region INTERFACE : IOutgoingRequestClient

    public TcpClient ServerSocket { get { return this.httpWebServerSocket; } set { } }

    #region Server connection

    virtual public void OpenServerConnection(string host)
    {
      Logging.Instance.LogMessage(this.requestObj.Id, Logging.Level.DEBUG, "TcpClientBase.OpenServerConnection()");

      if (string.IsNullOrEmpty(host))
      {
        throw new Exception("Host is invalid");
      }

      this.httpWebServerSocket = new TcpClient();
      this.httpWebServerSocket.NoDelay = true;
      this.httpWebServerSocket.Connect(host, this.remoteTcpPort);

      this.webServerStreamReader = new MyBinaryReader(this.httpWebServerSocket.GetStream(), 8192, Encoding.UTF8, this.requestObj.Id);
      this.webServerStreamWriter = new BinaryWriter(this.httpWebServerSocket.GetStream());
    }


    /// <summary>
    ///
    /// </summary>
    /// <param name="pNetworkStream"></param>
    public virtual void CloseServerConnection()
    {
      Logging.Instance.LogMessage(this.requestObj.Id, Logging.Level.DEBUG, "TcpClientBase.CloseServerConnection()");

      if (this.webServerStreamReader != null)
      {
        this.webServerStreamReader.Close();
      }

      if (this.webServerStreamWriter != null)
      {
        this.webServerStreamWriter.Close();
      }

      if (this.httpWebServerSocket != null)
      {
        this.httpWebServerSocket.Close();
      }
    }

    #endregion


    #region Server header transfer

    public void ForwardRequestC2S(string requestMethod, string path, string httpVersion)
    {
      if (string.IsNullOrEmpty(requestMethod))
      {
        throw new Exception("MethodString is invalid");
      }

      if (string.IsNullOrEmpty(path))
      {
        throw new Exception("Path is invalid");
      }

      if (string.IsNullOrEmpty(httpVersion))
      {
        throw new Exception("HTTP version is invalid");
      }

      string requestString = string.Format("{0} {1} {2}\r\n", requestMethod, path, httpVersion);
      byte[] requestByteArray = Encoding.UTF8.GetBytes(requestString);

      this.DumpstringDetails(requestMethod);
      this.DumpstringDetails(path);
      this.DumpstringDetails(httpVersion);

      this.webServerStreamWriter.Write(requestByteArray, 0, requestByteArray.Length);
      this.webServerStreamWriter.Flush();
    }


    public void ForwardHeadersC2S(Hashtable requestHeaders)
    {
      byte[] headerByteArray;
      string headerString;

      if (requestHeaders == null || requestHeaders.Keys.Count <= 0)
      {
        throw new Exception("Request headers are invalid");
      }

      // Send headers to server
      foreach (string tmpKey in requestHeaders.Keys)
      {
        headerString = string.Format("{0}: {1}{2}", tmpKey, requestHeaders[tmpKey], System.Environment.NewLine);
        headerByteArray = Encoding.UTF8.GetBytes(headerString);

        Logging.Instance.LogMessage(this.requestObj.Id, Logging.Level.DEBUG, "TcpClientBase.ForwardHeadersC2S(): Header Client2Server: {0}", headerString);
        this.webServerStreamWriter.Write(headerByteArray, 0, headerByteArray.Length);
      }

      // Send empty line to server to signalize "End of headerByteArray"
      headerByteArray = Encoding.UTF8.GetBytes("\r\n");
      this.webServerStreamWriter.Write(headerByteArray, 0, headerByteArray.Length);
      this.webServerStreamWriter.Flush();
    }


    public void ReadServerStatusLine(ServerStatusResponse serverStatusResponseObj)
    {
      string[] headerSplitter = new string[3];
      string serverStatusLine = this.webServerStreamReader.ReadLine(false);

      Logging.Instance.LogMessage(this.requestObj.Id, Logging.Level.INFO, "TcpClientBase.ReadServerStatusLine(): {0}", serverStatusLine);

      headerSplitter = serverStatusLine.Split(new char[] { ' ', '\t' }, 3);
      serverStatusResponseObj.HttpVersion = headerSplitter[0];
      serverStatusResponseObj.StatusCode = headerSplitter[1];
      serverStatusResponseObj.StatusDescription = headerSplitter[2];
    }


    public void ReadServerResponseHeaders(ServerResponseMetaData serverResponseMetaDataObj)
    {
      string[] headerTuple = new string[2];
      string key = string.Empty;
      string value = string.Empty;
      string dataLine = string.Empty;

      dataLine = this.webServerStreamReader.ReadLine(false);

      while (dataLine.Length > 0)
      {
        if (!dataLine.Contains(":"))
        {
          throw new ProxyErrorException("The server response header was invalid");
        }

        headerTuple = dataLine.Split(new char[] { ':' }, 2);
        key = headerTuple[0].Trim();
        value = headerTuple[1].Trim();

        Logging.Instance.LogMessage(this.requestObj.Id, Logging.Level.DEBUG, "TcpClientBase.ReadServerResponseHeaders(): Adding headerByteArray \"{0}\" with value \"{1}\" ", key, value);

        if (serverResponseMetaDataObj.ResponseHeaders.ContainsKey(key))
        {
          Logging.Instance.LogMessage(this.requestObj.Id, Logging.Level.DEBUG, "TcpClientBase.ReadServerResponseHeaders(): Header \"{0}\" (value:\"{1}\") already exists! Removing it now!", key, value);
          serverResponseMetaDataObj.ResponseHeaders.Remove(key);
        }

        if (key.ToLower() == "connection" && value.ToLower() == "keep-alive")
        {
          this.requestObj.IsServerKeepAlive = true;
        }
        else if (key.ToLower() == "connection" && value.ToLower() == "close")
        {
          this.requestObj.IsServerKeepAlive = false;
        }

        serverResponseMetaDataObj.ResponseHeaders.Add(key, value);
        dataLine = this.webServerStreamReader.ReadLine(false);
      }

      // Parse Client request content type
      try
      {
        serverResponseMetaDataObj.ContentTypeEncoding = this.DetermineServerResponseContentTypeEncoding(serverResponseMetaDataObj.ResponseHeaders);
      }
      catch (ProxyWarningException pex)
      {
        Logging.Instance.LogMessage(this.requestObj.Id, Logging.Level.INFO, "TcpClientBase.ReadServerResponseHeaders(Exception): Could not determine content type: {0}", pex.Message);
        Logging.Instance.LogMessage(this.requestObj.Id, Logging.Level.INFO, "TcpClientBase.ReadServerResponseHeaders(Exception): Setting default content type=text/html, charset=UTF-8");

        serverResponseMetaDataObj.ContentTypeEncoding.ContentType = "text/html";
        serverResponseMetaDataObj.ContentTypeEncoding.ContentCharSet = "UTF-8";
        serverResponseMetaDataObj.ContentTypeEncoding.ContentCharsetEncoding = Encoding.GetEncoding(serverResponseMetaDataObj.ContentTypeEncoding.ContentCharSet);
      }

      Logging.Instance.LogMessage(this.requestObj.Id, Logging.Level.DEBUG, "TcpClientBase.ReadServerResponseHeaders(): Server response content type: {0}, {1}", serverResponseMetaDataObj.ContentTypeEncoding.ContentType, serverResponseMetaDataObj.ContentTypeEncoding.ContentCharSet);

      // Parse Client request content length
      this.DetermineServerResponseContentLength(serverResponseMetaDataObj);
      Logging.Instance.LogMessage(this.requestObj.Id, Logging.Level.DEBUG, "TcpClientBase.ReadServerResponseHeaders(): Server response content length: {0}", serverResponseMetaDataObj.ContentLength);
    }

    #endregion


    #region Client header transfer

    public void ForwardStatusLineS2C(ServerStatusResponse serverStatusResponseObj)
    {
      string statusLineStr = string.Format("{0} {1} {2}\r\n", serverStatusResponseObj.HttpVersion, serverStatusResponseObj.StatusCode, serverStatusResponseObj.StatusDescription);
      byte[] statusLineByteArr = Encoding.UTF8.GetBytes(statusLineStr);

      this.clientStreamWriter.Write(statusLineByteArr, 0, statusLineByteArr.Length);
    }


    public void ForwardHeadersS2C(Hashtable serverResponseHeaders)
    {
      string header;
      byte[] headerByteArr;
      foreach (string tmpKey in serverResponseHeaders.Keys)
      {
        header = string.Format("{0}: {1}{2}", tmpKey, serverResponseHeaders[tmpKey].ToString(), System.Environment.NewLine);
        headerByteArr = Encoding.UTF8.GetBytes(header);
        this.clientStreamWriter.Write(headerByteArr, 0, headerByteArr.Length);
        this.clientStreamWriter.Flush();
      }

      // Send empty line to server to signalize "End of headerByteArray"
      headerByteArr = Encoding.UTF8.GetBytes("\r\n");
      this.clientStreamWriter.Write(headerByteArr, 0, headerByteArr.Length);
      this.clientStreamWriter.Flush();
    }

    #endregion


    #region Client/Server data transfer

    public void RelayDataC2S(bool mustBeProcessed, SniffedDataChunk sniffedDataChunk)
    {
      byte[] buffer = new byte[MaxBufferSize];

      // 1. No data to relay/process
      if (this.requestObj.ProxyDataTransmissionModeC2S == DataTransmissionMode.NoDataToTransfer)
      {
        Logging.Instance.LogMessage(this.requestObj.Id, Logging.Level.DEBUG, "TcpClienBase.RelayDataC2S(): DataTransmissionMode.NoDataToTransfer");

      // 2.0 Unknow amount of data chunks is transferred from the tcpClient to the peer system because
      // -   HTTP headerByteArray "Transfer-Encoding" was set
      }
      else if (this.requestObj.ProxyDataTransmissionModeC2S == DataTransmissionMode.Chunked && !mustBeProcessed)
      {
        Logging.Instance.LogMessage(this.requestObj.Id, Logging.Level.DEBUG, "TcpClienBase.RelayDataC2S(): DataTransmissionMode.Chunked, processed:false");
        this.ForwardChunkedNonprocessedDataToPeer(this.clientStreamReader, this.webServerStreamWriter, sniffedDataChunk);

      // 2.1 Unknow amount of data chunks is transferred from the tcpClient to the peer system because
      // -   HTTP headerByteArray "Transfer-Encoding" was set
      }
      else if (this.requestObj.ProxyDataTransmissionModeC2S == DataTransmissionMode.ContentLength && !mustBeProcessed)
      {
        Logging.Instance.LogMessage(this.requestObj.Id, Logging.Level.DEBUG, "TcpClienBase.RelayDataC2S(): DataTransmissionMode.ContentLength, processed:false");
        this.ForwardNonchunkedNonprocessedDataToPeer(this.clientStreamReader, this.webServerStreamWriter, this.requestObj.ClientRequestObj.ClientRequestContentLength, sniffedDataChunk);

      // 2.2 Unknow amount of data chunks is transferred from the tcpClient to the peer system because
      // -   HTTP headerByteArray "Transfer-Encoding" was set
      }
      else if (this.requestObj.ProxyDataTransmissionModeC2S == DataTransmissionMode.ReadOneLine && !mustBeProcessed)
      {
        Logging.Instance.LogMessage(this.requestObj.Id, Logging.Level.DEBUG, "TcpClienBase.RelayDataC2S(): DataTransmissionMode.ReadOneLine, processed:false");
        this.ForwardSingleLineNonprocessedDataToPeer(this.clientStreamReader, this.webServerStreamWriter, sniffedDataChunk);






      // 3.0 Unknow amount of data chunks is transferred from the tcpClient to the peer system because
      // -   HTTP headerByteArray "Transfer-Encoding" was set
      }
      else if (this.requestObj.ProxyDataTransmissionModeC2S == DataTransmissionMode.Chunked && mustBeProcessed)
      {
        Logging.Instance.LogMessage(this.requestObj.Id, Logging.Level.DEBUG, "TcpClienBase.RelayDataC2S(): DataTransmissionMode.Chunked, processed:true");
        this.ForwardChunkedProcessedDataChunks(this.clientStreamReader, this.webServerStreamWriter, this.requestObj.ClientRequestObj.ContentTypeEncoding.ContentCharsetEncoding, sniffedDataChunk);


      // 3.1 Predefined amount of data is transferred from the tcpClient to the peer system because
      // -   HTTP headerByteArray "Content-Length" was defined
      }
      else if (this.requestObj.ProxyDataTransmissionModeC2S == DataTransmissionMode.ContentLength && mustBeProcessed)
      {
        Logging.Instance.LogMessage(this.requestObj.Id, Logging.Level.DEBUG, "TcpClienBase.RelayDataC2S(): DataTransmissionMode.ContentLength, ContentLength:{0}, processed:true", this.requestObj.ClientRequestObj.ClientRequestContentLength);
        this.ForwardNonchunkedProcessedDataToPeer(this.clientStreamReader, this.webServerStreamWriter, this.requestObj.ClientRequestObj.ClientRequestContentLength, sniffedDataChunk);

      // 3.2 Unknow amount of data chunks is transferred from the tcpClient to the peer system because
      // -   HTTP headerByteArray "Transfer-Encoding" was set
      }
      else if (this.requestObj.ProxyDataTransmissionModeC2S == DataTransmissionMode.ReadOneLine && mustBeProcessed)
      {
        Logging.Instance.LogMessage(this.requestObj.Id, Logging.Level.DEBUG, "TcpClienBase.RelayDataC2S(): DataTransmissionMode.ReadOneLine, processed:true");
        this.ForwardSingleLineProcessedDataToPeer(this.clientStreamReader, this.webServerStreamWriter, sniffedDataChunk);





      // 4 Predefined amount of data is transferred from the tcpClient to the peer system because
      // -   HTTP headerByteArray "Content-Length" was defined
      }
      else if (this.requestObj.ProxyDataTransmissionModeC2S == DataTransmissionMode.RelayBlindly)
      {
        Logging.Instance.LogMessage(this.requestObj.Id, Logging.Level.DEBUG, "TcpClienBase.RelayDataC2S(): DataTransmissionMode.ContentLength, ContentLength:{0}, processed:true", this.requestObj.ClientRequestObj.ClientRequestContentLength);
        this.BlindlyRelayData(this.clientStreamReader, this.webServerStreamWriter, sniffedDataChunk);

      // 5 This state actually should never happen! No idea what to do at this point :/
      }
      else
      {
        Logging.Instance.LogMessage(this.requestObj.Id, Logging.Level.DEBUG, "TcpClienBase.RelayDataC2S(): ContentLength:{0}, processed:false", this.requestObj.ClientRequestObj.ClientRequestContentLength);
        this.ForwardNonchunkedNonprocessedDataToPeer(this.clientStreamReader, this.webServerStreamWriter, this.requestObj.ClientRequestObj.ClientRequestContentLength, sniffedDataChunk);
      }
    }


    public void RelayDataS2C(bool mustBeProcessed)
    {
      byte[] buffer = new byte[MaxBufferSize];

      // 1. No data to relay/process
      if (this.requestObj.ProxyDataTransmissionModeS2C == DataTransmissionMode.NoDataToTransfer)
      {
        Logging.Instance.LogMessage(this.requestObj.Id, Logging.Level.DEBUG, "TcpClienBase.RelayDataS2C(): DataTransmissionMode.NoDataToTransfer");





      // 2.0 Unknow amount of data chunks is transferred from the tcpClient to the peer system because
      // -   HTTP headerByteArray "Transfer-Encoding" was set
      }
      else if (this.requestObj.ProxyDataTransmissionModeS2C == DataTransmissionMode.Chunked && !mustBeProcessed)
      {
        Logging.Instance.LogMessage(this.requestObj.Id, Logging.Level.DEBUG, "TcpClienBase.RelayDataS2C(): DataTransmissionMode.Chunked, processed:false");
        this.ForwardChunkedNonprocessedDataToPeer(this.webServerStreamReader, this.clientStreamWriter);


      // 2.1 Unknow amount of data chunks is transferred from the tcpClient to the peer system because
      // -   HTTP headerByteArray "Transfer-Encoding" was set
      }
      else if (this.requestObj.ProxyDataTransmissionModeS2C == DataTransmissionMode.ContentLength && !mustBeProcessed)
      {
        Logging.Instance.LogMessage(this.requestObj.Id, Logging.Level.DEBUG, "TcpClienBase.RelayDataS2C(): DataTransmissionMode.ContentLength, processed:false");
        // this.ForwardChunkedNonprocessedDataToPeer(this.webServerStreamReader, this.clientStreamWriter);
        this.ForwardNonchunkedNonprocessedDataToPeer(this.webServerStreamReader, this.clientStreamWriter, this.requestObj.ServerResponseMetaDataObj.ContentLength);



      // 3.0 Unknow amount of data chunks is transferred from the tcpClient to the peer system because
      // -   HTTP headerByteArray "Transfer-Encoding" was set
      }
      else if (this.requestObj.ProxyDataTransmissionModeS2C == DataTransmissionMode.Chunked && mustBeProcessed)
      {
        Logging.Instance.LogMessage(this.requestObj.Id, Logging.Level.DEBUG, "TcpClienBase.RelayDataS2C(): DataTransmissionMode.Chunked, processed:true");
        this.ForwardChunkedProcessedDataChunks(this.webServerStreamReader, this.clientStreamWriter, this.requestObj.ServerResponseMetaDataObj.ContentTypeEncoding.ContentCharsetEncoding);


      // 3.1 Predefined amount of data is transferred from the tcpClient to the peer system because
      // -   HTTP headerByteArray "Content-Length" was defined
      }
      else if (this.requestObj.ProxyDataTransmissionModeS2C == DataTransmissionMode.ContentLength && mustBeProcessed)
      {
        Logging.Instance.LogMessage(this.requestObj.Id, Logging.Level.DEBUG, "TcpClienBase.RelayDataS2C(): DataTransmissionMode.ContentLength, ContentLength:{0}, processed:true", this.requestObj.ServerResponseMetaDataObj.ContentLength);
        this.ForwardNonchunkedProcessedDataToPeer(this.webServerStreamReader, this.clientStreamWriter, this.requestObj.ServerResponseMetaDataObj.ContentLength);






      // 4 Predefined amount of data is transferred from the tcpClient to the peer system because
      // -   HTTP headerByteArray "Content-Length" was defined
      }
      else if (this.requestObj.ProxyDataTransmissionModeS2C == DataTransmissionMode.RelayBlindly)
      {
        Logging.Instance.LogMessage(this.requestObj.Id, Logging.Level.DEBUG, "TcpClienBase.RelayDataS2C(): DataTransmissionMode.ContentLength, ContentLength:{0}, processed:true", this.requestObj.ServerResponseMetaDataObj.ContentLength);
        this.BlindlyRelayData(this.webServerStreamReader, this.clientStreamWriter);

      // 5 This state actually should never happen! No idea what to do at this point :/
      }
      else
      {
        Logging.Instance.LogMessage(this.requestObj.Id, Logging.Level.DEBUG, "TcpClienBase.RelayDataS2C(): ContentLength:{0}, processed:false", this.requestObj.ServerResponseMetaDataObj.ContentLength);
        this.ForwardNonchunkedNonprocessedDataToPeer(this.webServerStreamReader, this.clientStreamWriter, this.requestObj.ServerResponseMetaDataObj.ContentLength);
      }
    }

    #endregion

    #endregion


    #region PUBLIC

    public TcpClientBase(RequestObj requestObj, int remoteTcpPort) :
      base(requestObj)
    {
      this.requestObj = requestObj;
      this.remoteTcpPort = remoteTcpPort;
    }

    #region Client side

    #endregion


    #endregion


    #region PRIVATE

    private void DumpstringDetails(string data)
    {
      if (string.IsNullOrEmpty(data))
      {
        return;
      }

      string hexResult = this.StringToHex(data);
      try
      {
        hexResult = hexResult.Trim();
      }
      catch
      {
      }

      Logging.Instance.LogMessage(this.requestObj.Id, Logging.Level.DEBUG, "TcpClientBase.DumpstringDetails():  |{0}| |{1}|", data, hexResult);
    }


    private void DetermineServerResponseContentLength(ServerResponseMetaData serverResponseMetaDataObj)
    {
      try
      {
        if (serverResponseMetaDataObj.ResponseHeaders.ContainsKey("Content-Length"))
        {
          string contentLen = serverResponseMetaDataObj.ResponseHeaders["Content-Length"].ToString();
          serverResponseMetaDataObj.ContentLength = int.Parse(contentLen);
        }
        else
        {
          serverResponseMetaDataObj.ContentLength = 0;
        }
      }
      catch (Exception ex)
      {
        Logging.Instance.LogMessage(this.requestObj.Id, Logging.Level.ERROR, "TcpClientBase.ReceiveServerResponsetHeaders(EXCEPTION/1): {0}", ex.Message);
        serverResponseMetaDataObj.ContentLength = 0;
      }
    }


    private DataContentTypeEncoding DetermineServerResponseContentTypeEncoding(Hashtable headers)
    {
      DataContentTypeEncoding contentTypeEncoding = new DataContentTypeEncoding();

      if (headers == null)
      {
        throw new ProxyWarningException("The headers list is invalid");
      }

      if (!headers.ContainsKey("Content-Type"))
      {
        throw new ProxyWarningException("The content type headerByteArray is invalid");
      }

      if (string.IsNullOrEmpty(headers["Content-Type"].ToString()))
      {
        throw new ProxyWarningException("The content type headerByteArray is invalid");
      }

      // If there is no content type headerByteArray set the default values
      if (!headers.ContainsKey("Content-Type") ||
          string.IsNullOrEmpty(headers["Content-Type"].ToString()))
      {
        contentTypeEncoding.ContentType = "text/html";
        contentTypeEncoding.ContentCharSet = "UTF-8";
        contentTypeEncoding.ContentCharsetEncoding = Encoding.GetEncoding(contentTypeEncoding.ContentCharSet);

        Logging.Instance.LogMessage(this.requestObj.Id, Logging.Level.DEBUG, "TcpClientBase.DetermineClientRequestContentTypeEncoding(): No Content-Type header found: text/html, UTF-8");
        return contentTypeEncoding;
      }

      // Parse the server response content type
      try
      {
        string contentType = headers["Content-Type"].ToString();

        if (contentType.Contains(";"))
        {
          string[] splitter = contentType.Split(new char[] { ';' }, 2);
          contentTypeEncoding.ContentType = splitter[0];
          contentTypeEncoding.ContentCharSet = this.DetermineContentCharSet(splitter[1]);
          contentTypeEncoding.ContentCharsetEncoding = Encoding.GetEncoding(contentTypeEncoding.ContentCharSet);
          Logging.Instance.LogMessage(this.requestObj.Id, Logging.Level.DEBUG, "TcpClientBase.DetermineClientRequestContentTypeEncoding(): Content-Type/Charset header found: {0}, {1}", contentTypeEncoding.ContentType, contentTypeEncoding.ContentCharSet);
        }
        else
        {
          contentTypeEncoding.ContentType = contentType;
          contentTypeEncoding.ContentCharSet = "UTF-8";
          contentTypeEncoding.ContentCharsetEncoding = Encoding.GetEncoding(contentTypeEncoding.ContentCharSet);
          Logging.Instance.LogMessage(this.requestObj.Id, Logging.Level.DEBUG, "TcpClientBase.DetermineClientRequestContentTypeEncoding(): Content-Type (noCharset) header found: {0}, {1}", contentTypeEncoding.ContentType, contentTypeEncoding.ContentCharSet);
        }
      }
      catch (Exception ex)
      {
        contentTypeEncoding.ContentType = "text/html";
        contentTypeEncoding.ContentCharSet = "UTF-8";
        contentTypeEncoding.ContentCharsetEncoding = Encoding.GetEncoding(contentTypeEncoding.ContentCharSet);
        Logging.Instance.LogMessage(this.requestObj.Id, Logging.Level.DEBUG, "TcpClientBase.DetermineClientRequestContentTypeEncoding(Exception): text/html, UTF-8 {0}", ex.Message);
      }

      return contentTypeEncoding;
    }


    private string DetermineContentCharSet(string httpContentTypeHeader)
    {
      string determinedCharSet = "UTF-8";

      if (string.IsNullOrEmpty(httpContentTypeHeader))
      {
        throw new Exception("The char set headerByteArray is invalid");
      }

      httpContentTypeHeader = httpContentTypeHeader.Trim();
      if (Regex.Match(httpContentTypeHeader, @"^charset\s*=", RegexOptions.IgnoreCase).Success)
      {
        string[] splitter = httpContentTypeHeader.Split(new char[] { '=' }, 2);
        determinedCharSet = splitter[1];
      }

      return determinedCharSet;
    }


    private string StringToHex(string hexstring)
    {
      var sb = new StringBuilder();
      foreach (char t in hexstring)
      {
        sb.Append(Convert.ToInt32(t).ToString("x") + " ");
      }

      return sb.ToString();
    }

    #endregion

  }
}
