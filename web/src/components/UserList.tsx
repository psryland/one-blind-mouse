import { useState } from 'react';
import { User, Display_Name } from '../types';
import './UserList.css';

interface UserListProps {
	users: User[];
	current_user_id: string;
	on_name_change?: (name: string) => void;
}

export default function UserList({ users, current_user_id, on_name_change }: UserListProps) {
	const [editing, set_editing] = useState(false);

	return (
		<div className="user-list">
			<div className="user-list-header">
				<h3>Online Users</h3>
				<span className="user-list-count">{users.length}</span>
				<span className="user-list-live">
					<span className="user-list-live-dot" />
					Live
				</span>
			</div>
			<ul className="user-list-items">
				{users.map((user) => {
					const is_current = user.user_id === current_user_id;
					return (
						<li
							key={user.user_id}
							className={`user-list-item${is_current ? ' is-current' : ''}`}
						>
							<span
								className="user-list-dot"
								style={{ backgroundColor: user.colour }}
							/>
							{is_current && editing ? (
								<input
									className="user-list-name-input"
									type="text"
									autoFocus
									defaultValue={user.name}
									maxLength={32}
									onBlur={(e) => {
										set_editing(false);
										const val = e.target.value.trim();
										if (val && on_name_change) on_name_change(val);
									}}
									onKeyDown={(e) => {
										if (e.key === 'Enter') (e.target as HTMLInputElement).blur();
										if (e.key === 'Escape') set_editing(false);
									}}
								/>
							) : (
								<span
									className={is_current && on_name_change ? 'user-list-name editable' : 'user-list-name'}
									onClick={() => { if (is_current && on_name_change) set_editing(true); }}
								>
									{Display_Name(user.name)}
								</span>
							)}
							{is_current && !editing && <span className="user-list-you">(you)</span>}
						</li>
					);
				})}
			</ul>
		</div>
	);
}
