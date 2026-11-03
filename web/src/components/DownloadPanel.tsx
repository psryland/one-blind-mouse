import { useState, useCallback } from 'react';
import './DownloadPanel.css';

interface DownloadInfo {
	platform: string;
	label: string;
	filename: string;
}

const DOWNLOADS: DownloadInfo[] = [
	{
		platform: 'win',
		label: 'Windows (x64)',
		filename: 'ThreeBlindMice.Windows.exe',
	},
	{
		platform: 'linux',
		label: 'Linux (x64)',
		filename: 'three-blind-mice-linux-x64.zip',
	},
];

function Detect_Platform(): string {
	const ua = navigator.userAgent.toLowerCase();
	if (ua.includes('win')) return 'win';
	if (ua.includes('linux')) return 'linux';
	return 'win';
}

interface DownloadPanelProps {
	session_code?: string;
}

export default function DownloadPanel({ session_code }: DownloadPanelProps) {
	const [detected_platform] = useState(Detect_Platform);
	const [show_all, set_show_all] = useState(false);

	const primary = DOWNLOADS.find((d) => d.platform === detected_platform) ?? DOWNLOADS[0];
	const others = DOWNLOADS.filter((d) => d.platform !== detected_platform);

	// Downloads the exe with the session code embedded in the filename
	const handle_download = useCallback((d: DownloadInfo) => {
		if (session_code && d.filename.endsWith('.exe')) {
			// Fetch as blob so we can rename the download
			const ext = d.filename.substring(d.filename.lastIndexOf('.'));
			const base = d.filename.substring(0, d.filename.lastIndexOf('.'));
			const coded_name = `${base}.${session_code}${ext}`;
			fetch(`/downloads/${d.filename}`)
				.then(r => r.blob())
				.then(blob => {
					const url = URL.createObjectURL(blob);
					const a = document.createElement('a');
					a.href = url;
					a.download = coded_name;
					document.body.appendChild(a);
					a.click();
					a.remove();
					URL.revokeObjectURL(url);
				});
		} else {
			// Direct download for non-exe files
			const a = document.createElement('a');
			a.href = `/downloads/${d.filename}`;
			a.download = d.filename;
			document.body.appendChild(a);
			a.click();
			a.remove();
		}
	}, [session_code]);

	return (
		<div className="download-panel-inline">
			<p className="download-inline-hint">
				Don't have the host app? Download it below.
			</p>
			<div className="download-buttons">
				<button
					className="download-btn primary"
					onClick={() => handle_download(primary)}
				>
					⬇ {primary.label}
				</button>

				{!show_all && others.length > 0 && (
					<button className="download-btn secondary" onClick={() => set_show_all(true)}>
						Other platforms
					</button>
				)}

				{show_all && others.map((d) => (
					<button
						key={d.platform}
						className="download-btn secondary"
						onClick={() => handle_download(d)}
					>
						⬇ {d.label}
					</button>
				))}
			</div>
		</div>
	);
}
