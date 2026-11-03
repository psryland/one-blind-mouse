// Azure Web PubSub client service for Three Blind Mice

// --- Message types matching the application protocol ---

export interface CursorMessage {
	type: 'cursor';
	user_id: string;
	name: string;
	colour: string;
	x: number;
	y: number;
	button: number;
}

export interface JoinMessage {
	type: 'join';
	user_id: string;
	name: string;
	colour: string;
}

export interface LeaveMessage {
	type: 'leave';
	user_id: string;
}

export interface HostConfigMessage {
	type: 'host_config';
	aspect_ratio: number;
	monitor_name: string;
}

export interface MonitorInfo {
	index: number;
	device: string;
	left: number;
	top: number;
	width: number;
	height: number;
	is_primary: boolean;
}

export interface WindowInfo {
	title: string;
	left: number;
	top: number;
	width: number;
	height: number;
	monitor_index: number;
}

export interface HostInfoMessage {
	type: 'host_info';
	monitors: MonitorInfo[];
	windows: WindowInfo[];
}

export interface HostThumbnailMessage {
	type: 'host_thumbnail';
	monitor_index: number;
	data_url: string;
}

export interface HostConstraintMessage {
	type: 'host_constraint';
	mode: 'monitor' | 'window' | 'rectangle';
	monitor_index?: number;
	window_title?: string;
	left?: number;
	top?: number;
	width?: number;
	height?: number;
}

export interface HostPickWindowMessage {
	type: 'host_pick_window';
}

export interface HostWindowPickedMessage {
	type: 'host_window_picked';
	title: string;
	left: number;
	top: number;
	width: number;
	height: number;
	monitor_index: number;
}

export interface HostPickRectangleMessage {
	type: 'host_pick_rectangle';
}

export interface HostHeartbeatMessage {
	type: 'host_heartbeat';
}

export interface HostEndMessage {
	type: 'host_end';
}

export interface HostRectanglePickedMessage {
	type: 'host_rectangle_picked';
	left: number;
	top: number;
	width: number;
	height: number;
}

export type PubSubMessage = CursorMessage | JoinMessage | LeaveMessage | HostConfigMessage | HostInfoMessage | HostThumbnailMessage | HostConstraintMessage | HostWindowPickedMessage | HostRectanglePickedMessage;

// Outbound-only messages (web → host) that aren't expected as inbound
type OutboundMessage = PubSubMessage | HostPickWindowMessage | HostPickRectangleMessage | HostHeartbeatMessage | HostEndMessage;

// --- Azure Web PubSub JSON subprotocol envelope types ---

interface SendToGroupEnvelope {
	type: 'sendToGroup';
	group: string;
	dataType: 'json';
	data: OutboundMessage;
}

interface JoinGroupEnvelope {
	type: 'joinGroup';
	group: string;
}

// --- Negotiate API response ---

interface NegotiateResponse {
	url: string;
}

// --- PubSubClient ---

const SUBPROTOCOL = 'json.webpubsub.azure.v1';
const THROTTLE_MS = 16; // ~60fps for responsive cursor movement
const MAX_BACKOFF_MS = 30_000;
const INITIAL_BACKOFF_MS = 1_000;

export class PubSubClient {
	private m_negotiate_url: string;
	private m_ws: WebSocket | null = null;
	private m_session_code: string = '';
	private m_user_id: string = '';
	private m_name: string = '';
	private m_colour: string = '';
	private m_connected: boolean = false;
	private m_should_reconnect: boolean = false;
	private m_reconnect_delay: number = INITIAL_BACKOFF_MS;
	private m_reconnect_timer: ReturnType<typeof setTimeout> | null = null;
	private m_message_callback: ((msg: PubSubMessage) => void) | null = null;
	private m_connection_callback: ((connected: boolean) => void) | null = null;

	// Throttle state for send_cursor
	private m_last_send_time: number = 0;
	private m_pending_cursor_timer: ReturnType<typeof setTimeout> | null = null;

	constructor(negotiate_url: string = '/api/negotiate') {
		this.m_negotiate_url = negotiate_url;
	}

	/**
	 * Connect to a session via Azure Web PubSub.
	 * Calls the negotiate endpoint to obtain a WebSocket URL, then opens the connection.
	 */
	async connect(session_code: string, user_id: string, name: string, colour: string): Promise<void> {
		this.m_session_code = session_code;
		this.m_user_id = user_id;
		this.m_name = name;
		this.m_colour = colour;
		this.m_should_reconnect = true;
		this.m_reconnect_delay = INITIAL_BACKOFF_MS;

		await this.Open_Connection();
	}

	/**
	 * Disconnect from the session gracefully.
	 */
	disconnect(): void {
		this.m_should_reconnect = false;
		this.Clear_Reconnect_Timer();
		this.Clear_Pending_Cursor();

		if (this.m_ws && this.m_ws.readyState === WebSocket.OPEN) {
			// Send leave message before closing
			const leave_msg: LeaveMessage = {
				type: 'leave',
				user_id: this.m_user_id,
			};
			this.Send_To_Group(leave_msg);
			this.m_ws.close();
		}

		this.m_ws = null;
		this.Set_Connected(false);
	}

	/**
	 * Update the display name for future messages.
	 */
	set_name(name: string): void {
		this.m_name = name;
	}

	/**
	 * Send a cursor position update to the session group, throttled to ~20fps.
	 */
	send_cursor(x: number, y: number, button: number): void {
		const msg: CursorMessage = {
			type: 'cursor',
			user_id: this.m_user_id,
			name: this.m_name,
			colour: this.m_colour,
			x,
			y,
			button,
		};

		const now = performance.now();
		const elapsed = now - this.m_last_send_time;

		if (elapsed >= THROTTLE_MS) {
			// Enough time has passed — send immediately
			this.Clear_Pending_Cursor();
			this.m_last_send_time = now;
			this.Send_To_Group(msg);
		} else {
			// Schedule a send at the end of the throttle window
			this.Clear_Pending_Cursor();
			this.m_pending_cursor_timer = setTimeout(() => {
				this.m_pending_cursor_timer = null;
				this.m_last_send_time = performance.now();
				this.Send_To_Group(msg);
			}, THROTTLE_MS - elapsed);
		}
	}

	/**
	 * Send a host constraint message to tell the host app which region to constrain the overlay to.
	 */
	send_host_constraint(constraint: Omit<HostConstraintMessage, 'type'>): void {
		const msg: HostConstraintMessage = {
			type: 'host_constraint',
			...constraint,
		};
		this.Send_To_Group(msg);
	}

	/**
	 * Ask the host app to enter window-pick mode.
	 */
	send_host_pick_window(): void {
		this.Send_To_Group({ type: 'host_pick_window' } as HostPickWindowMessage);
	}

	/**
	 * Ask the host app to enter rectangle-select mode.
	 */
	send_host_pick_rectangle(): void {
		this.Send_To_Group({ type: 'host_pick_rectangle' } as HostPickRectangleMessage);
	}

	/**
	 * Periodic heartbeat so the host app knows the session is alive.
	 */
	send_host_heartbeat(): void {
		this.Send_To_Group({ type: 'host_heartbeat' } as HostHeartbeatMessage);
	}

	/**
	 * Tell the host app the session has ended.
	 */
	send_host_end(): void {
		this.Send_To_Group({ type: 'host_end' } as HostEndMessage);
	}

	/**
	 * Register a callback for incoming application messages.
	 */
	on_message(callback: (msg: PubSubMessage) => void): void {
		this.m_message_callback = callback;
	}

	/**
	 * Register a callback for connection state changes.
	 */
	on_connection_change(callback: (connected: boolean) => void): void {
		this.m_connection_callback = callback;
	}

	// --- Private helpers ---

	private async Open_Connection(): Promise<void> {
		const url = await this.Negotiate();
		if (!url) return;

		this.m_ws = new WebSocket(url, SUBPROTOCOL);

		this.m_ws.onopen = () => {
			this.m_reconnect_delay = INITIAL_BACKOFF_MS;
			this.Set_Connected(true);

			// Join the session group
			const join_group: JoinGroupEnvelope = {
				type: 'joinGroup',
				group: this.m_session_code,
			};
			this.m_ws!.send(JSON.stringify(join_group));

			// Announce presence
			const join_msg: JoinMessage = {
				type: 'join',
				user_id: this.m_user_id,
				name: this.m_name,
				colour: this.m_colour,
			};
			this.Send_To_Group(join_msg);
		};

		this.m_ws.onmessage = (event: MessageEvent) => {
			this.Handle_Message(event);
		};

		this.m_ws.onclose = () => {
			this.Set_Connected(false);
			this.Schedule_Reconnect();
		};

		this.m_ws.onerror = () => {
			// onclose will fire after onerror, which handles reconnection
			this.m_ws?.close();
		};
	}

	private async Negotiate(): Promise<string | null> {
		try {
			const params = new URLSearchParams({
				session: this.m_session_code,
				user: this.m_user_id,
			});
			const response = await fetch(`${this.m_negotiate_url}?${params}`, {
				method: 'GET',
			});
			if (!response.ok) {
				console.error(`Negotiate failed: ${response.status}`);
				return null;
			}
			const data: NegotiateResponse = await response.json();
			return data.url;
		} catch (err) {
			console.error('Negotiate error:', err);
			return null;
		}
	}

	private Send_To_Group(data: OutboundMessage): void {
		if (!this.m_ws || this.m_ws.readyState !== WebSocket.OPEN) return;

		const envelope: SendToGroupEnvelope = {
			type: 'sendToGroup',
			group: this.m_session_code,
			dataType: 'json',
			data,
		};
		this.m_ws.send(JSON.stringify(envelope));
	}

	private Handle_Message(event: MessageEvent): void {
		if (!this.m_message_callback) return;

		try {
			const envelope = JSON.parse(event.data as string);

			// Azure Web PubSub JSON subprotocol wraps group messages
			if (envelope.type === 'message' && envelope.data) {
				this.m_message_callback(envelope.data as PubSubMessage);
			}
		} catch {
			// Ignore malformed messages
		}
	}

	private Set_Connected(connected: boolean): void {
		if (this.m_connected === connected) return;
		this.m_connected = connected;
		this.m_connection_callback?.(connected);
	}

	private Schedule_Reconnect(): void {
		if (!this.m_should_reconnect) return;

		this.Clear_Reconnect_Timer();
		this.m_reconnect_timer = setTimeout(async () => {
			this.m_reconnect_timer = null;
			await this.Open_Connection();
		}, this.m_reconnect_delay);

		// Exponential backoff
		this.m_reconnect_delay = Math.min(this.m_reconnect_delay * 2, MAX_BACKOFF_MS);
	}

	private Clear_Reconnect_Timer(): void {
		if (this.m_reconnect_timer !== null) {
			clearTimeout(this.m_reconnect_timer);
			this.m_reconnect_timer = null;
		}
	}

	private Clear_Pending_Cursor(): void {
		if (this.m_pending_cursor_timer !== null) {
			clearTimeout(this.m_pending_cursor_timer);
			this.m_pending_cursor_timer = null;
		}
	}
}
