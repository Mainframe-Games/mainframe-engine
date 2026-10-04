/*
 * ENet smoke test: version check plus a reliable loopback round trip (connect, send, receive, disconnect).
 */
#include <stdio.h>
#include <string.h>

#include "enet.h"

#define CHECK(cond)                                                              \
	do                                                                           \
	{                                                                            \
		if (!(cond))                                                             \
		{                                                                        \
			fprintf(stderr, "FAIL %s:%d: %s\n", __FILE__, __LINE__, #cond);      \
			return 1;                                                            \
		}                                                                        \
	} while (0)

static const char payload[] = "mainframe-enet-smoke";

/* Services both hosts until `want` is seen on `target`, or ~5 s pass. Returns the event type seen. */
static ENetEventType pump(ENetHost* a, ENetHost* b, ENetHost* target, ENetEventType want, ENetEvent* out)
{
	for (int i = 0; i < 500; ++i)
	{
		ENetHost* hosts[2] = {a, b};
		for (int h = 0; h < 2; ++h)
		{
			ENetEvent ev;
			while (enet_host_service(hosts[h], &ev, 5) > 0)
			{
				if (hosts[h] == target && ev.type == want)
				{
					*out = ev;
					return want;
				}
				if (ev.type == ENET_EVENT_TYPE_RECEIVE)
					enet_packet_dispose(ev.packet);
			}
		}
	}
	return ENET_EVENT_TYPE_NONE;
}

int main(void)
{
	CHECK(enet_initialize() == 0);

	const ENetVersion v = enet_linked_version();
	printf("linked ENet %u.%u.%u\n", ENET_VERSION_GET_MAJOR(v), ENET_VERSION_GET_MINOR(v), ENET_VERSION_GET_PATCH(v));
	CHECK(v == ENET_VERSION_CREATE(2, 4, 8));

	ENetAddress bind_address;
	memset(&bind_address, 0, sizeof bind_address);
	bind_address.port = 0; /* ephemeral */
	ENetHost* server = enet_host_create(&bind_address, 4, 2, 0, 0, 0);
	CHECK(server != NULL);
	CHECK(server->address.port != 0);

	ENetHost* client = enet_host_create(NULL, 1, 2, 0, 0, 0);
	CHECK(client != NULL);

	ENetAddress server_address;
	memset(&server_address, 0, sizeof server_address);
	CHECK(enet_address_set_ip(&server_address, "127.0.0.1") == 0);
	server_address.port = server->address.port;

	ENetPeer* peer = enet_host_connect(client, &server_address, 2, 0);
	CHECK(peer != NULL);

	ENetEvent ev;
	CHECK(pump(server, client, client, ENET_EVENT_TYPE_CONNECT, &ev) == ENET_EVENT_TYPE_CONNECT);

	ENetPacket* packet = enet_packet_create(payload, sizeof payload, ENET_PACKET_FLAG_RELIABLE);
	CHECK(packet != NULL);
	CHECK(enet_peer_send(peer, 1, packet) == 0);
	enet_host_flush(client);

	CHECK(pump(server, client, server, ENET_EVENT_TYPE_RECEIVE, &ev) == ENET_EVENT_TYPE_RECEIVE);
	CHECK(ev.channelID == 1);
	CHECK(enet_packet_get_length(ev.packet) == (int)sizeof payload);
	CHECK(memcmp(enet_packet_get_data(ev.packet), payload, sizeof payload) == 0);
	enet_packet_dispose(ev.packet);

	enet_peer_disconnect(peer, 0);
	CHECK(pump(server, client, client, ENET_EVENT_TYPE_DISCONNECT, &ev) == ENET_EVENT_TYPE_DISCONNECT);

	enet_host_destroy(client);
	enet_host_destroy(server);
	enet_deinitialize();

	printf("OK: ENet loopback round trip\n");
	return 0;
}
