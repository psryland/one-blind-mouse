// Session and user types for Three Blind Mice

export interface User {
	user_id: string;
	name: string;
	colour: string;
}

export interface SessionState {
	session_code: string;
	is_host: boolean;
	users: User[];
	connected: boolean;
}

// Colour palette for auto-assigning cursor colours
export const CURSOR_COLOURS = [
	'#E53935', // red
	'#1E88E5', // blue
	'#43A047', // green
	'#8E24AA', // purple
	'#FB8C00', // orange
	'#00ACC1', // teal
	'#D81B60', // pink
	'#3949AB', // indigo
	'#F4511E', // deep orange
	'#00897B', // dark teal
	'#7B1FA2', // deep purple
	'#039BE5', // light blue
];

export function Generate_Session_Code(): string {
	const chars = 'abcdefghijklmnopqrstuvwxyz0123456789';
	let code = '';
	const array = new Uint8Array(6);
	crypto.getRandomValues(array);
	for (const byte of array) {
		code += chars[byte % chars.length];
	}
	return code;
}

const ADJECTIVES = [
	'Swift', 'Clever', 'Brave', 'Calm', 'Dizzy', 'Eager', 'Fancy', 'Gentle',
	'Happy', 'Jolly', 'Kind', 'Lucky', 'Mighty', 'Noble', 'Plucky', 'Quick',
	'Rusty', 'Sneaky', 'Tiny', 'Witty', 'Zesty', 'Bold', 'Crisp', 'Daring',
	'Fierce', 'Grand', 'Hasty', 'Icy', 'Jazzy', 'Keen', 'Lofty', 'Merry',
];

const NOUNS = [
	'Mouse', 'Fox', 'Owl', 'Bear', 'Wolf', 'Hawk', 'Panda', 'Otter',
	'Raven', 'Hare', 'Lynx', 'Finch', 'Crane', 'Badger', 'Robin', 'Viper',
	'Moose', 'Cobra', 'Falcon', 'Gecko', 'Ibis', 'Jackal', 'Koala', 'Lemur',
	'Newt', 'Osprey', 'Quail', 'Sloth', 'Tiger', 'Wombat', 'Yak', 'Ferret',
];

export function Generate_Random_Name(): string {
	const adj_bytes = new Uint8Array(1);
	const noun_bytes = new Uint8Array(1);
	crypto.getRandomValues(adj_bytes);
	crypto.getRandomValues(noun_bytes);
	return `${ADJECTIVES[adj_bytes[0] % ADJECTIVES.length]} ${NOUNS[noun_bytes[0] % NOUNS.length]}`;
}

/// If a user types "Anonymous", we fix that for them 🐭
export function Display_Name(name: string): string {
	return name.toLowerCase() === 'anonymous' ? 'Anonymouse 🐭' : name;
}
