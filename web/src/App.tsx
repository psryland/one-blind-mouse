import { useState, useCallback, useEffect, useRef, useMemo } from 'react';
import SessionPanel from './components/SessionPanel';
import MouseCanvas from './components/MouseCanvas';
import UserList from './components/UserList';
import ConstrainPanel from './components/ConstrainPanel';
import DownloadPanel from './components/DownloadPanel';
import { PubSubClient, PubSubMessage, MonitorInfo, WindowInfo, HostConstraintMessage } from './services/pubsub';
import { User, Generate_Session_Code, Generate_Random_Name, CURSOR_COLOURS } from './types';
import './App.css';

const STORAGE_KEY_NAME = 'tbm_user_name';

interface PickedRect {
	left: number;
	top: number;
	width: number;
	height: number;
}

function Generate_User_Id(): string {
	const chars = 'abcdefghijklmnopqrstuvwxyz0123456789';
	let id = '';
	const array = new Uint8Array(8);
	crypto.getRandomValues(array);
	for (const byte of array) {
		id += chars[byte % chars.length];
	}
	return id;
}

function App() {
	const [session_code, set_session_code] = useState<string | null>(null);
	const [is_host, set_is_host] = useState(false);
	const [connected, set_connected] = useState(false);
	const [users, set_users] = useState<User[]>([]);
	const [aspect_ratio, set_aspect_ratio] = useState(16 / 9);
	const [user_name, set_user_name] = useState(() => {
		const stored = localStorage.getItem(STORAGE_KEY_NAME);
		return stored && stored.trim() ? stored : Generate_Random_Name();
	});

	// Host-specific state: populated when host_info message arrives from the Host App
	const [host_connected, set_host_connected] = useState(false);
	const [host_monitors, set_host_monitors] = useState<MonitorInfo[]>([]);
	const [host_windows, set_host_windows] = useState<WindowInfo[]>([]);
	const [monitor_thumbnails, set_monitor_thumbnails] = useState<Map<number, string>>(new Map());

	// Picker state
	const [picking_window, set_picking_window] = useState(false);
	const [picking_rectangle, set_picking_rectangle] = useState(false);
	const [picked_window_title, set_picked_window_title] = useState<string | null>(null);
	const [picked_rectangle, set_picked_rectangle] = useState<PickedRect | null>(null);

	// Stable user identity for the session
	const user_id = useMemo(() => Generate_User_Id(), []);
	const user_colour = useMemo(() => CURSOR_COLOURS[Math.floor(Math.random() * CURSOR_COLOURS.length)], []);

	const client_ref = useRef<PubSubClient | null>(null);
	const heartbeat_ref = useRef<ReturnType<typeof setInterval> | null>(null);

	// Host heartbeat — lets the host app detect when the session ends
	useEffect(() => {
		if (is_host && connected && client_ref.current) {
			heartbeat_ref.current = setInterval(() => {
				client_ref.current?.send_host_heartbeat();
			}, 10_000);
		}
		return () => {
			if (heartbeat_ref.current) {
				clearInterval(heartbeat_ref.current);
				heartbeat_ref.current = null;
			}
		};
	}, [is_host, connected]);

	// Persist name to localStorage when it changes
	useEffect(() => {
		localStorage.setItem(STORAGE_KEY_NAME, user_name);
	}, [user_name]);

	const handle_message = useCallback((msg: PubSubMessage) => {
		switch (msg.type) {
			case 'join':
				set_users((prev) => {
					if (prev.some((u) => u.user_id === msg.user_id)) return prev;
					return [...prev, { user_id: msg.user_id, name: msg.name, colour: msg.colour }];
				});
				break;
			case 'leave':
				set_users((prev) => prev.filter((u) => u.user_id !== msg.user_id));
				break;
			case 'cursor':
				// Update user entry if we see a cursor from someone not yet in the list
				set_users((prev) => {
					const idx = prev.findIndex((u) => u.user_id === msg.user_id);
					if (idx === -1) {
						return [...prev, { user_id: msg.user_id, name: msg.name, colour: msg.colour }];
					}
					return prev;
				});
				break;
			case 'host_config':
				if (msg.aspect_ratio > 0) {
					set_aspect_ratio(msg.aspect_ratio);
				}
				break;
			case 'host_info':
				set_host_connected(true);
				set_host_monitors(msg.monitors);
				set_host_windows(msg.windows);
				break;
			case 'host_thumbnail':
				set_monitor_thumbnails((prev) => {
					const next = new Map(prev);
					next.set(msg.monitor_index, msg.data_url);
					return next;
				});
				break;
			case 'host_window_picked':
				set_picking_window(false);
				set_picked_window_title(msg.title);
				break;
			case 'host_rectangle_picked':
				set_picking_rectangle(false);
				set_picked_rectangle({ left: msg.left, top: msg.top, width: msg.width, height: msg.height });
				break;
		}
	}, []);

	const handle_start_session = useCallback(() => {
		const code = Generate_Session_Code();
		set_session_code(code);
		set_is_host(true);
		set_host_connected(false);
		set_host_monitors([]);
		set_host_windows([]);
		set_monitor_thumbnails(new Map());
		set_users([{ user_id, name: user_name, colour: user_colour }]);
		set_aspect_ratio(16 / 9);

		const client = new PubSubClient();
		client_ref.current = client;
		client.on_message(handle_message);
		client.on_connection_change((is_connected) => set_connected(is_connected));
		client.connect(code, user_id, user_name, user_colour);

		// Download host app with session code embedded in the filename
		fetch('/downloads/ThreeBlindMice.Windows.exe')
			.then(r => r.blob())
			.then(blob => {
				const url = URL.createObjectURL(blob);
				const a = document.createElement('a');
				a.href = url;
				a.download = `ThreeBlindMice.Windows.${code}.exe`;
				document.body.appendChild(a);
				a.click();
				a.remove();
				URL.revokeObjectURL(url);
			})
			.catch(() => { /* download failed — user can use the manual download button */ });
	}, [user_id, user_name, user_colour, handle_message]);

	const handle_join = useCallback((code: string) => {
		set_session_code(code);
		set_is_host(false);
		set_users([{ user_id, name: user_name, colour: user_colour }]);
		set_aspect_ratio(16 / 9);

		const client = new PubSubClient();
		client_ref.current = client;
		client.on_message(handle_message);
		client.on_connection_change((is_connected) => set_connected(is_connected));
		client.connect(code, user_id, user_name, user_colour);
	}, [user_id, user_name, user_colour, handle_message]);

	const handle_leave = useCallback(() => {
		// Tell the host app the session is over
		if (is_host) client_ref.current?.send_host_end();
		client_ref.current?.disconnect();
		client_ref.current = null;
		set_session_code(null);
		set_is_host(false);
		set_connected(false);
		set_users([]);
		set_host_connected(false);
		set_host_monitors([]);
		set_host_windows([]);
		set_monitor_thumbnails(new Map());

		// Clear ?session= from URL so auto-join doesn't re-trigger on landing
		window.history.replaceState({}, '', window.location.pathname);
	}, [is_host]);

	const handle_cursor_move = useCallback((x: number, y: number, button: number) => {
		client_ref.current?.send_cursor(x, y, button);
	}, []);

	const handle_constraint_select = useCallback((constraint: Omit<HostConstraintMessage, 'type'>) => {
		client_ref.current?.send_host_constraint(constraint);
	}, []);

	const handle_pick_window = useCallback(() => {
		set_picking_window(true);
		set_picked_window_title(null);
		client_ref.current?.send_host_pick_window();
	}, []);

	const handle_pick_rectangle = useCallback(() => {
		set_picking_rectangle(true);
		set_picked_rectangle(null);
		client_ref.current?.send_host_pick_rectangle();
	}, []);

	// Handle name change during an active session
	const handle_name_change_in_session = useCallback((name: string) => {
		set_user_name(name);
		set_users((prev) => prev.map((u) => u.user_id === user_id ? { ...u, name } : u));
		client_ref.current?.set_name(name);
	}, [user_id]);

	// Clean up on unmount
	useEffect(() => {
		return () => {
			client_ref.current?.disconnect();
		};
	}, []);

	const show_canvas = session_code !== null && !is_host && connected;
	const show_host_waiting = session_code !== null && is_host && !host_connected;
	const show_host_active = session_code !== null && is_host && host_connected;

	// Landing page: no session active
	if (session_code === null) {
		return (
			<div className="app">
				<header className="app-header">
					<h1>Three Blind Mice</h1>
					<span className="app-header-url">https://three-blind-mice.rylogic.co.nz</span>
				</header>

				<div className="app-body">
					<div className="app-landing">
						<SessionPanel
							mode="landing"
							user_name={user_name}
							on_name_change={set_user_name}
							on_start_session={handle_start_session}
							on_join={handle_join}
						/>
					</div>
				</div>
			</div>
		);
	}

	// Active session views (host or remote)
	return (
		<div className={`app${show_canvas ? ' app-remote-view' : ''}`}>
			<header className="app-header">
				<h1>Three Blind Mice</h1>
				<span className="app-header-url">https://three-blind-mice.rylogic.co.nz</span>
			</header>

			<div className="app-body">
				<div className="app-layout">
					<div className="app-left">
						{/* Host waiting for Host App to connect */}
						{show_host_waiting && (
							<div className="host-waiting">
								<div className="host-waiting-spinner" />
								<h2>Waiting for Host App…</h2>
								<div className="host-waiting-code">{session_code}</div>
								<p className="host-waiting-hint">
									Run the downloaded host app to connect.
									If the download didn't start, use the button below.
								</p>
								<DownloadPanel session_code={session_code} />
							</div>
						)}

						{/* Host session active: show ConstrainPanel with real monitor data */}
						{show_host_active && (
							<ConstrainPanel
								monitors={host_monitors}
								thumbnails={monitor_thumbnails}
								windows={host_windows}
								on_constraint_select={handle_constraint_select}
								on_pick_window={handle_pick_window}
								on_pick_rectangle={handle_pick_rectangle}
								picking_window={picking_window}
								picking_rectangle={picking_rectangle}
								picked_window_title={picked_window_title}
								picked_rectangle={picked_rectangle}
							/>
						)}

						{/* Remote user: show MouseCanvas */}
						{show_canvas && (
							<MouseCanvas
								aspect_ratio={aspect_ratio}
								on_cursor_move={handle_cursor_move}
								user_name={user_name}
								user_colour={user_colour}
							/>
						)}

						{/* Connecting state */}
						{session_code && !connected && (
							<div className="canvas-placeholder">
								<p>Connecting to session <strong>{session_code}</strong>…</p>
							</div>
						)}
					</div>

					<div className="app-right">
						<SessionPanel
							mode="active"
							session_code={session_code}
							on_leave={handle_leave}
						/>

						{session_code && (
							<UserList users={users} current_user_id={user_id} on_name_change={handle_name_change_in_session} />
						)}
					</div>
				</div>
			</div>
		</div>
	);
}

export default App;
