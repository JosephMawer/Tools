#define HAVE_REMOTE

#include <pcap.h>
#include <Shlwapi.h>
#include <windows.h>

#include "APE.h"
#include "DnsHelper.h"
#include "DnsPoisoning.h"
#include "DnsStructs.h"
#include "LinkedListSpoofedDnsHosts.h"
#include "Logging.h"
#include "NetworkHelperFunctions.h"


extern PHOSTNODE gDnsSpoofingList;


DWORD WINAPI DnsResponseSniffer(LPVOID lpParam)
{
  DWORD retVal = 0;
  pcap_t *ifcReadHandle = NULL;
  PSCANPARAMS scanParams = (PSCANPARAMS)lpParam;
  char pcapErrorBuffer[PCAP_ERRBUF_SIZE];
  char filter[MAX_BUF_SIZE + 1];
  struct bpf_program filterCode;
  unsigned int netMask = 0;
  int pcapRetVal = 0;
  struct pcap_pkthdr *packetHeader = NULL;
  unsigned char *packetData = NULL;
  PETHDR ethrHdr = NULL;
  PIPHDR ipHdr = NULL;
  PUDPHDR udpHdr = NULL;
  int ipHdrLen = 0;
  char *dnsData = NULL;
  //PDNS_HDR dnsHdr = NULL;
  PDNS_HEADER dnsHdr = NULL;
  char dstIp[MAX_BUF_SIZE + 1];
  char srcIp[MAX_BUF_SIZE + 1];
  int dstPort = -1;
  int srcPort = -1;
  int counter = 0;
  u_char* urlPacket = NULL;
  u_char* urlTemp = NULL;

  ZeroMemory(pcapErrorBuffer, sizeof(pcapErrorBuffer));
  ZeroMemory(&filterCode, sizeof(filterCode));
  ZeroMemory(filter, sizeof(filter));

  // 0. Initialize sniffer
  if ((ifcReadHandle = pcap_open_live((char *)scanParams->InterfaceName, 65536, PCAP_OPENFLAG_NOCAPTURE_LOCAL | PCAP_OPENFLAG_MAX_RESPONSIVENESS, PCAP_READTIMEOUT, pcapErrorBuffer)) == NULL)
  {
    LogMsg(DBG_ERROR, "DnsResponseSniffer(): Unable to open the adapter: %s", pcapErrorBuffer);
    retVal = 1;
    goto END;
  }

  _snprintf(filter, sizeof(filter) - 1, "src port 53 and dst host %s", scanParams->LocalIpStr);
  netMask = 0xffffff;

  if (pcap_compile((pcap_t *)ifcReadHandle, &filterCode, (const char *)filter, 1, netMask) < 0)
  {
    LogMsg(DBG_ERROR, "DnsResponseSniffer(): Unable to compile the BPF filter \"%s\"", filter);
    retVal = 6;
    goto END;
  }

  if (pcap_setfilter((pcap_t *)ifcReadHandle, &filterCode) < 0)
  {
    LogMsg(DBG_ERROR, "DnsResponseSniffer(): Unable to set the BPF filter \"%s\"", filter);
    retVal = 7;
    goto END;
  }

  // Start intercepting data packets.
  while ((pcapRetVal = pcap_next_ex(ifcReadHandle, (struct pcap_pkthdr **) &packetHeader, &packetData)) >= 0)
  {
    if (pcapRetVal == 0)
    {
      continue;
    }
    else if (pcapRetVal < 0)
    {
      printf("Error reading the packets: %s\n", pcap_geterr(ifcReadHandle));
      break;
    }

    ethrHdr = (PETHDR)packetData;
    if (ethrHdr == NULL || 
        htons(ethrHdr->ether_type) != ETHERTYPE_IP)
    {
      continue;
    }

    ipHdr = (PIPHDR)(packetData + 14);
    if (ipHdr == NULL || 
        ipHdr->proto != IP_PROTO_UDP)
    {
      continue;
    }

    ipHdrLen = (ipHdr->ver_ihl & 0xf) * 4;
    if (ipHdrLen <= 0)
    {
      continue;
    }

    udpHdr = (PUDPHDR)((unsigned char*)ipHdr + ipHdrLen);
    if (udpHdr == NULL || 
        udpHdr->ulen <= 0 || 
        ntohs(udpHdr->sport) != UDP_DNS)
    {
      continue;
    }

    dnsData = ((char*)udpHdr + sizeof(UDPHDR));
    dnsHdr = (PDNS_HEADER)&dnsData[sizeof(DNS_HEADER)];

    if (dnsHdr == NULL)
    {
      continue;
    }

    if (ntohs(dnsHdr->q_count) <= 0)
    {
      continue;
    }

    ZeroMemory(dstIp, sizeof(dstIp));
    ZeroMemory(srcIp, sizeof(srcIp));

    IpBin2String((unsigned char *)&ipHdr->daddr, (unsigned char *)dstIp, sizeof(dstIp) - 1);
    IpBin2String((unsigned char *)&ipHdr->saddr, (unsigned char *)srcIp, sizeof(srcIp) - 1);
    dstPort = ntohs(udpHdr->dport);
    srcPort = ntohs(udpHdr->sport);

    urlTemp = (u_char*)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, 2 * MAX_BUF_SIZE + 2);
    urlPacket = (u_char*)(dnsHdr + sizeof(DNS_HEADER));
    for (counter = 0; urlPacket[counter] != 0; counter++)
    {
      urlTemp[counter] = urlPacket[counter];
    }

    urlTemp[counter] = 0;
    counter++;

printf("DNS: %s:%d -> %s:%d id:0x%04x, icount:%d ...\n", srcIp, srcPort, dstIp, dstPort, dnsHdr->id, counter);
  }

END:

  if (ifcReadHandle)
  {
    pcap_close(ifcReadHandle);
  }

  return retVal;
}


BOOL GetReqHostName(unsigned char *packetParam, int packetLengthParam, char *hostnameParam, int hostBufferLengthParam)
{
  BOOL retVal = TRUE;
  PETHDR etherHdrPtr = NULL;
  PIPHDR ipHdrPtr = NULL;     // ip header
  PUDPHDR udpHdrPtr = NULL;   // udp header                              
  PDNS_HEADER dnsHdrPtr = NULL; // dns header
  char *data = NULL;
  int ipHdrLength = 0;
  int dataLength = 0;
  int index1;
  int count2;

  etherHdrPtr = (PETHDR)packetParam;
  ipHdrPtr = (PIPHDR)((unsigned char*)packetParam + sizeof(ETHDR));
  ipHdrLength = (ipHdrPtr->ver_ihl & 0xf) * 4;
  udpHdrPtr = (PUDPHDR)((unsigned char*)ipHdrPtr + ipHdrLength);
  //dnsHdrPtr = (PDNS_HDR) ((unsigned char*) udpHdrPtr + sizeof(UDPHDR));
  dnsHdrPtr = (PDNS_HEADER)((unsigned char*)udpHdrPtr + sizeof(UDPHDR));
  //data = (char *)((unsigned char*)dnsHdrPtr + sizeof(DNS_HDR));
  data = (char *)((unsigned char*)dnsHdrPtr + sizeof(DNS_HEADER));


  // Extract host name
  //if ((dataLength = packetLengthParam - (sizeof(ETHDR) + ipHdrLength + sizeof(UDPHDR) + sizeof(PDNS_HDR))) > 0)
  if ((dataLength = packetLengthParam - (sizeof(ETHDR) + ipHdrLength + sizeof(UDPHDR) + sizeof(PDNS_HEADER))) > 0)
  {
    count2 = 0;

    for (index1 = 1; index1 < dataLength && count2 < hostBufferLengthParam; index1++)
    {
      if (data[index1] > 31 && data[index1] < 127)
      {
        hostnameParam[count2++] = data[index1];
      }
      else if (data[index1] == '\0')
      {
        break;
      }
      else
      {
        hostnameParam[count2++] = '.';
      }
    }
  }

  if (count2 > 2)
  {
    retVal = TRUE;
  }

  return retVal;
}

