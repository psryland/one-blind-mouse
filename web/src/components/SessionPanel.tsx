import { useState, useEffect, useCallback } from 'react';
import './SessionPanel.css';

// Landing mode: shown when no session is active
interface LandingProps {
	mode: 'landing';
	user_name: string;
	on_name_change: (name: string) => void;
	on_start_session: () => void;
	on_join: (code: string) => void;
}

// Active mode: shown when a session is in progress
interface ActiveProps {
	mode: 'active';
	session_code: string;
	on_leave: () => void;
}

type SessionPanelProps = LandingProps | ActiveProps;

export default function SessionPanel(props: SessionPanelProps) {
	const [join_input, set_join_input] = useState('');
	const [code_icon, set_code_icon] = useState('📋');
	const [link_icon, set_link_icon] = useState('🔗');

	// Auto-join from ?session= query parameter on mount (only in landing mode)
	const on_join_ref = props.mode === 'landing' ? props.on_join : undefined;
	useEffect(() => {
		if (!on_join_ref) return;
		const params = new URLSearchParams(window.location.search);
		const session_param = params.get('session');
		if (session_param) {
			on_join_ref(session_param.toLowerCase());
		}
	}, []); // eslint-disable-line react-hooks/exhaustive-deps

	const handle_join = useCallback(() => {
		if (props.mode !== 'landing') return;
		const code = join_input.trim().toLowerCase();
		if (code.length >= 4) {
			props.on_join(code);
			set_join_input('');
		}
	}, [join_input, props]);

	// Active session view
	if (props.mode === 'active') {
		const shareable_url = `https://three-blind-mice.rylogic.co.nz?session=${props.session_code}`;

		const handle_copy_code = async () => {
			try {
				await navigator.clipboard.writeText(props.session_code);
				set_code_icon('✅');
				setTimeout(() => set_code_icon('📋'), 1500);
			} catch { /* ignore */ }
		};

		const handle_copy_link = async () => {
			try {
				await navigator.clipboard.writeText(shareable_url);
				set_link_icon('✅');
				setTimeout(() => set_link_icon('🔗'), 1500);
			} catch { /* ignore */ }
		};

		return (
			<div className="session-panel">
				<div className="session-active">
					<div className="session-code-row">
						<span className="session-code-label">Session Code:</span>
						<span className="session-code">{props.session_code}</span>
						<button className="copy-icon-btn" onClick={handle_copy_code} title="Copy session code">{code_icon}</button>
					</div>
					<div className="shareable-url">
						<input type="text" readOnly value={shareable_url} onClick={(e) => (e.target as HTMLInputElement).select()} />
						<button className="copy-icon-btn" onClick={handle_copy_link} title="Copy link">{link_icon}</button>
					</div>
					<button className="leave-btn" onClick={props.on_leave}>Leave Session</button>
				</div>
			</div>
		);
	}

	// Landing view
	return (
		<div className="session-panel">
			<div className="session-section">
				<h3>Start Session</h3>
				<p className="session-section-hint">Host a session and see remote user's mouse pointers on your screen</p>
				<button className="btn-primary" onClick={props.on_start_session}>Start Session</button>
			</div>

			<div className="session-divider">or</div>

			<div className="session-section">
				<h3>Join Session</h3>

				<div className="name-input-inline">
					<label htmlFor="user-name">Your Name</label>
					<input
						id="user-name"
						type="text"
						placeholder="Enter your name"
						value={props.user_name}
						onChange={(e) => props.on_name_change(e.target.value)}
						maxLength={32}
					/>
				</div>

				<div className="join-controls">
					<input
						type="text"
						placeholder="Enter session code"
						value={join_input}
						onChange={(e) => set_join_input(e.target.value)}
						onKeyDown={(e) => e.key === 'Enter' && handle_join()}
						maxLength={8}
					/>
					<button onClick={handle_join} disabled={join_input.trim().length < 4}>
						Join
					</button>
				</div>
			</div>
		</div>
	);
}
