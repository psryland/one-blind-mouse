import { useRef, useState, useCallback, useEffect } from 'react';
import { Display_Name } from '../types';
import './MouseCanvas.css';

interface MouseCanvasProps {
	aspect_ratio?: number;
	on_cursor_move: (x: number, y: number, button: number) => void;
	user_name: string;
	user_colour: string;
}

const MIN_ZOOM = 1;
const MAX_ZOOM = 8;
const ZOOM_STEP = 1.2; // multiplicative step per wheel notch

export default function MouseCanvas({
	aspect_ratio = 16 / 9,
	on_cursor_move,
	user_name,
	user_colour,
}: MouseCanvasProps) {
	const canvas_ref = useRef<HTMLDivElement>(null);
	const [is_locked, set_is_locked] = useState(false);
	const [cursor_x, set_cursor_x] = useState(0.5);
	const [cursor_y, set_cursor_y] = useState(0.5);
	const [button_state, set_button_state] = useState(0);
	const [zoom_level, set_zoom_level] = useState(1);

	// Track accumulated movement while pointer-locked
	const accum_ref = useRef({ x: 0.5, y: 0.5 });
	const zoom_ref = useRef(1); // mirror for use in non-stale callbacks

	// Resize the canvas div to fill parent while maintaining aspect ratio
	const [canvas_size, set_canvas_size] = useState({ width: 640, height: 360 });
	const wrapper_ref = useRef<HTMLDivElement>(null);

	useEffect(() => {
		const wrapper = wrapper_ref.current;
		if (!wrapper) return;

		const observer = new ResizeObserver((entries) => {
			for (const entry of entries) {
				const { width: max_w, height: max_h } = entry.contentRect;
				if (max_w <= 0 || max_h <= 0) return;

				let w = max_w;
				let h = w / aspect_ratio;
				if (h > max_h) {
					h = max_h;
					w = h * aspect_ratio;
				}
				set_canvas_size({ width: Math.floor(w), height: Math.floor(h) });
			}
		});

		observer.observe(wrapper);
		return () => observer.disconnect();
	}, [aspect_ratio]);

	// Listen for pointer lock changes
	useEffect(() => {
		const on_lock_change = () => {
			const locked = document.pointerLockElement === canvas_ref.current;
			set_is_locked(locked);
			if (!locked) {
				set_button_state(0);
				// Reset zoom when unlocking
				set_zoom_level(1);
				zoom_ref.current = 1;
			}
		};

		document.addEventListener('pointerlockchange', on_lock_change);
		return () => document.removeEventListener('pointerlockchange', on_lock_change);
	}, []);

	// Mouse wheel → zoom (only while pointer-locked)
	useEffect(() => {
		const el = canvas_ref.current;
		if (!el) return;

		const handle_wheel = (e: WheelEvent) => {
			e.preventDefault();
			const direction = e.deltaY < 0 ? 1 : -1; // scroll up = zoom in
			const new_zoom = Math.max(MIN_ZOOM, Math.min(MAX_ZOOM,
				zoom_ref.current * (direction > 0 ? ZOOM_STEP : 1 / ZOOM_STEP)));
			// Snap to 1.0 when close
			const snapped = Math.abs(new_zoom - 1) < 0.05 ? 1 : new_zoom;
			zoom_ref.current = snapped;
			set_zoom_level(snapped);
		};

		el.addEventListener('wheel', handle_wheel, { passive: false });
		return () => el.removeEventListener('wheel', handle_wheel);
	}, []);

	// Handle mouse move — zoom divides sensitivity for finer control
	const handle_mouse_move = useCallback((e: React.MouseEvent) => {
		const el = canvas_ref.current;
		if (!el) return;

		let nx: number;
		let ny: number;
		const z = zoom_ref.current;

		if (is_locked) {
			const acc = accum_ref.current;

			// Clamp movement deltas to prevent huge jumps (RDP, cursor warps, etc.)
			const max_delta = 80;
			const dx = Math.max(-max_delta, Math.min(max_delta, e.movementX));
			const dy = Math.max(-max_delta, Math.min(max_delta, e.movementY));

			acc.x = Math.max(0, Math.min(1, acc.x + dx / (canvas_size.width * z)));
			acc.y = Math.max(0, Math.min(1, acc.y + dy / (canvas_size.height * z)));
			nx = acc.x;
			ny = acc.y;
		} else {
			const rect = el.getBoundingClientRect();
			nx = Math.max(0, Math.min(1, (e.clientX - rect.left) / rect.width));
			ny = Math.max(0, Math.min(1, (e.clientY - rect.top) / rect.height));
		}

		set_cursor_x(nx);
		set_cursor_y(ny);
		on_cursor_move(nx, ny, button_state);
	}, [is_locked, canvas_size, button_state, on_cursor_move]);

	const handle_click = useCallback(() => {
		if (!is_locked && canvas_ref.current) {
			canvas_ref.current.requestPointerLock();
		}
	}, [is_locked]);

	const handle_mouse_down = useCallback((e: React.MouseEvent) => {
		if (e.button === 0) {
			set_button_state(1);
			on_cursor_move(cursor_x, cursor_y, 1);
		} else if (e.button === 2 && is_locked) {
			// Right-click releases pointer lock
			document.exitPointerLock();
		}
	}, [cursor_x, cursor_y, on_cursor_move, is_locked]);

	const handle_mouse_up = useCallback((e: React.MouseEvent) => {
		if (e.button === 0) {
			set_button_state(0);
			on_cursor_move(cursor_x, cursor_y, 0);
		}
	}, [cursor_x, cursor_y, on_cursor_move]);

	// Prevent context menu on right-click inside the canvas
	const handle_context_menu = useCallback((e: React.MouseEvent) => {
		e.preventDefault();
	}, []);

	const instruction_text = is_locked
		? 'Esc or right-click to release · Scroll to zoom'
		: 'Click to lock cursor';

	// Grid cell size scales with zoom: at 1x = 2% cells (50 lines), at 2x = 4% cells, etc.
	const grid_pct = 2 * zoom_level;
	const grid_style = {
		backgroundSize: `${grid_pct}% ${grid_pct}%`,
	};

	const show_zoom_badge = zoom_level > 1.05;

	return (
		<div className="mouse-canvas-wrapper" ref={wrapper_ref}>
			<div
				className={`mouse-canvas${is_locked ? ' locked' : ''}`}
				ref={canvas_ref}
				style={{ width: canvas_size.width, height: canvas_size.height }}
				onClick={handle_click}
				onMouseMove={handle_mouse_move}
				onMouseDown={handle_mouse_down}
				onMouseUp={handle_mouse_up}
				onContextMenu={handle_context_menu}
			>
				<div className="mouse-canvas-grid" style={grid_style} />

				<div className="mouse-canvas-instruction">{instruction_text}</div>

				{/* Zoom indicator badge */}
				{show_zoom_badge && (
					<div className="mouse-canvas-zoom-badge">
						🔍 {zoom_level.toFixed(1)}×
					</div>
				)}

				{/* User's own cursor dot */}
				<div
					className={`mouse-canvas-cursor${button_state === 1 ? ' laser-active' : ''}`}
					style={{
						left: `${cursor_x * 100}%`,
						top: `${cursor_y * 100}%`,
						backgroundColor: user_colour,
					}}
				>
					{button_state === 1 && (
						<div
							className="mouse-canvas-laser-glow"
							style={{ borderColor: user_colour, boxShadow: `0 0 12px 4px ${user_colour}` }}
						/>
					)}
					<span className="mouse-canvas-cursor-label">{Display_Name(user_name)}</span>
				</div>
			</div>
		</div>
	);
}
